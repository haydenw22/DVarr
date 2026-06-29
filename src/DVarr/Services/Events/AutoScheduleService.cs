using System.Text.Json;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services;
using Microsoft.EntityFrameworkCore;
using RecordingEntity = DVarr.Data.Entities.Recording;

namespace DVarr.Services.Events;

/// <summary>
/// The P1 layer that makes DVarr "Sportarr-like": periodically (a) refreshes events for monitored leagues
/// whose data is stale, and (b) creates Pending recordings for monitored events inside their league's
/// horizon via the resolver (pinned channel + same-credential fallbacks). The existing SchedulerService
/// then arms those recordings at pre-roll and the recorder captures them.
/// </summary>
public sealed class AutoScheduleService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DbWriteGate _gate;
    private readonly ILogger<AutoScheduleService> _log;

    private static readonly RecordingState[] Handled =
    {
        RecordingState.Pending, RecordingState.Starting, RecordingState.Recording, RecordingState.Recovering,
        RecordingState.FailingOver, RecordingState.Degraded, RecordingState.Stopping, RecordingState.Finalizing,
        RecordingState.Done, RecordingState.FinalizeRetry, RecordingState.NeedsAttention, RecordingState.Conflict,
        RecordingState.Cancelled,
    };

    // Recording states that occupy a credential's single stream slot (used for credit-aware conflict planning).
    private static readonly RecordingState[] SlotHolding =
    {
        RecordingState.Pending, RecordingState.Starting, RecordingState.Recording, RecordingState.Recovering,
        RecordingState.FailingOver, RecordingState.Degraded, RecordingState.Stopping, RecordingState.Finalizing,
    };

    public AutoScheduleService(IServiceScopeFactory scopes, DbWriteGate gate, ILogger<AutoScheduleService> log)
    { _scopes = scopes; _gate = gate; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        _log.LogInformation("[AutoSchedule] Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 300;
            try { interval = await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[AutoSchedule] Tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(interval, 60, 3600)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Parse League.MonitoredTeamsJson (a JSON array of {id,name}, or a plain ["id",...] array) into a set of
    /// TheSportsDB team ids. Empty / invalid / absent → empty set (= follow ALL teams).</summary>
    public static HashSet<string> ParseMonitoredTeamIds(string? json)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string? id = el.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Object when el.TryGetProperty("id", out var v)
                            => v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString(),
                        System.Text.Json.JsonValueKind.String => el.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(id)) set.Add(id!.Trim()); // trim to match SerializeTeams (stored trimmed)
                }
        }
        catch { /* malformed → treat as "all teams" */ }
        return set;
    }

    /// <summary>Parse League.MonitoredSessionsJson (a JSON array of session-kind strings, e.g. ["Race","Qualifying"])
    /// into a set. Empty / invalid / absent → empty set (= record ALL sessions). Mirrors team-follow: the full schedule
    /// is still ingested; this only filters which motorsport sessions the scheduler arms / the calendar shows.</summary>
    public static HashSet<string> ParseMonitoredSessions(string? json)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var k = el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString()
                          : el.ValueKind == System.Text.Json.JsonValueKind.Object && el.TryGetProperty("kind", out var v) ? v.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(k)) set.Add(k!.Trim());
                }
        }
        catch { /* malformed → treat as "all sessions" */ }
        return set;
    }

    private async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
        var ingest = scope.ServiceProvider.GetRequiredService<EventIngestService>();
        var planner = scope.ServiceProvider.GetRequiredService<CreditAwarePlanner>();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

        var now = EpochTime.Now();
        var interval = await settings.GetIntAsync("auto_schedule_interval_s"); if (interval <= 0) interval = 300;
        var syncInterval = await settings.GetIntAsync("event_sync_interval_s"); if (syncInterval <= 0) syncInterval = 21600;
        var pre = await settings.GetIntAsync("default_pre_pad_s"); if (pre <= 0) pre = 300;
        var post = await settings.GetIntAsync("default_post_pad_s"); if (post <= 0) post = 1800;

        // 1) Refresh events for monitored, non-manual leagues whose data is stale.
        var dueLeagues = await db.Leagues
            .Where(l => l.Monitored && l.EventProvider != "manual" &&
                        (l.LastEventSyncUtc == null || now - l.LastEventSyncUtc > syncInterval))
            .Select(l => l.Id).ToListAsync(ct);
        foreach (var lid in dueLeagues)
        {
            try { await ingest.SyncLeagueAsync(lid, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] event sync failed for league {Id}", lid); }
        }

        // 2) Auto-schedule monitored events within each league's horizon, spreading across both logins.
        var monitoredLeagues = await db.Leagues.AsNoTracking().Where(l => l.Monitored).ToDictionaryAsync(l => l.Id, ct);
        if (monitoredLeagues.Count == 0) return interval;
        var maxLookahead = now + 60L * 86400; // hard ceiling; per-league horizon applied below

        var candidates = await db.Events.AsNoTracking()
            .Where(e => e.Monitored && monitoredLeagues.Keys.Contains(e.LeagueId)
                        && e.StartUtc > now - 3600 && e.StartUtc < maxLookahead
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Live || e.Status == EventStatus.Unknown))
            .OrderBy(e => e.StartUtc).Take(500).ToListAsync(ct);

        // Team-follow: a league with MonitoredTeamsJson set records ONLY those teams' matches. (The full schedule is
        // still ingested so episode numbers stay correct — this filters only what the scheduler arms.) Keep an event
        // if its league follows all teams (no list) OR either side's team id is in the followed set. A revived event
        // (1c) re-enters this same candidate pool, so it's covered without a separate guard.
        var followedTeams = monitoredLeagues.Values
            .Select(l => (l.Id, Ids: ParseMonitoredTeamIds(l.MonitoredTeamsJson)))
            .Where(x => x.Ids.Count > 0).ToDictionary(x => x.Id, x => x.Ids);
        if (followedTeams.Count > 0)
            candidates = candidates.Where(e =>
                !followedTeams.TryGetValue(e.LeagueId, out var ids)
                || (e.HomeTeamId != null && ids.Contains(e.HomeTeamId))
                || (e.AwayTeamId != null && ids.Contains(e.AwayTeamId))
                // Fail OPEN for an event with NO team ids at all (e.g. a not-yet-drawn final, or pre-v1.19 data): we
                // can't tell who's playing, so don't silently drop it — better to over-record than miss a real match.
                || (e.HomeTeamId == null && e.AwayTeamId == null)).ToList();

        // Session-follow (motorsport): a league with MonitoredSessionsJson arms only those session kinds. Keep an event
        // if its league follows all sessions, OR its title classifies to a followed kind (fail-open if unclassifiable).
        var followedSessions = monitoredLeagues.Values
            .Select(l => (l.Id, Kinds: ParseMonitoredSessions(l.MonitoredSessionsJson)))
            .Where(x => x.Kinds.Count > 0).ToDictionary(x => x.Id, x => x.Kinds);
        if (followedSessions.Count > 0)
            candidates = candidates.Where(e =>
                !followedSessions.TryGetValue(e.LeagueId, out var kinds)
                || MotorsportSession.Classify(e.Title) is not { } k
                || kinds.Contains(k)).ToList();

        var handledEventIds = (await db.Recordings
            .Where(r => r.EventId != null && Handled.Contains(r.State))
            .Select(r => r.EventId!.Value).Distinct().ToListAsync(ct)).ToHashSet();

        // ---- Credit-aware conflict planning ----
        // Current occupancy: every slot-holding recording reserves [Start-PrePad, End+PostPad] on its credential.
        // AsNoTracking: these are read-only planning snapshots — every actual mutation re-loads the row fresh inside the
        // write gate (db.Recordings.FindAsync), so we avoid accumulating a large tracked graph across the 500-event tick.
        var committed = await db.Recordings.AsNoTracking().Where(r => SlotHolding.Contains(r.State)).ToListAsync(ct);
        var conflicts = await db.Recordings.AsNoTracking().Where(r => r.State == RecordingState.Conflict).ToListAsync(ct);

        // Pending recordings keyed by event, so a re-synced event that MOVED can retime its already-created recording
        // (else it would record at the stale time). Only Pending is safe to retime — never an in-progress capture.
        var pendingByEvent = committed
            .Where(r => r.State == RecordingState.Pending && r.EventId != null)
            .GroupBy(r => r.EventId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // League priority for ranking existing recordings (their event's league) — for the conflict ladder.
        var rankEventIds = committed.Concat(conflicts).Where(r => r.EventId != null).Select(r => r.EventId!.Value).Distinct().ToList();
        var evLeague = await db.Events.Where(e => rankEventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.LeagueId, ct);
        var leaguePrio = await db.Leagues.ToDictionaryAsync(l => l.Id, l => l.Priority, ct);
        int LeaguePrioOf(int? eventId) => eventId is { } eid && evLeague.TryGetValue(eid, out var lid) && leaguePrio.TryGetValue(lid, out var p) ? p : 0;
        CreditAwarePlanner.PRank RankOf(RecordingEntity r) => CreditAwarePlanner.MakeRank(r.Priority, LeaguePrioOf(r.EventId), r.StartUtc, r.Id);

        var slots = committed
            .Select(r => new CreditAwarePlanner.Slot(r.SourceId, r.StartUtc - r.PrePadS, r.EndUtc + r.PostPadS, r.Id, r.State, RankOf(r)))
            .ToList();

        // Persist a placement: fallbacks are ranks 2..N on the SAME credential as the recording (composite FK).
        async Task WriteFallbacksAsync(int recId, int sourceId, List<(int channelId, int sourceId)> fbs)
        {
            await db.RecordingFallbacks.Where(f => f.RecordingId == recId).ExecuteDeleteAsync(ct);
            var rank = 2;
            foreach (var fb in fbs.Where(f => f.sourceId == sourceId).DistinctBy(f => f.channelId))
                db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = recId, Rank = rank++, ChannelId = fb.channelId, SourceId = fb.sourceId });
        }
        // Park a (still-Pending) recording in Conflict because a higher-priority event preempted its slot.
        async Task PreemptAsync(int victimId, string why)
        {
            var v = await db.Recordings.FindAsync(victimId);
            if (v is null || v.State != RecordingState.Pending) return;
            v.State = RecordingState.Conflict; v.FailureReason = why; v.UpdatedUtc = now;
            db.Notifications.Add(new Notification { RecordingId = victimId, TsUtc = now, Kind = NotificationKind.Conflict, Severity = Severity.Warn, ToState = "Conflict", Message = why });
            slots.RemoveAll(s => s.RecordingId == victimId);
        }

        int placed = 0, conflicted = 0, promoted = 0;

        // 1b) Stale-status cleanup: an event that flipped to Cancelled/Postponed after its recording was created is
        // no longer a candidate (the query excludes those statuses), so cancel its not-yet-started recordings here.
        // NOT Completed — "FT" often arrives mid-broadcast and we keep full endings ([[keep-full-race-endings]]).
        var deadEventIds = await db.Events
            .Where(e => e.Status == EventStatus.Cancelled || e.Status == EventStatus.Postponed)
            .Select(e => e.Id).ToListAsync(ct);
        if (deadEventIds.Count > 0)
        {
            var staleRecIds = await db.Recordings
                .Where(r => r.EventId != null && deadEventIds.Contains(r.EventId.Value)
                            && (r.State == RecordingState.Pending || r.State == RecordingState.Conflict))
                .Select(r => r.Id).ToListAsync(ct);
            foreach (var rid in staleRecIds)
            {
                await _gate.WriteAsync(async () =>
                {
                    var rec = await db.Recordings.FindAsync(rid);
                    if (rec is null || rec.State is not (RecordingState.Pending or RecordingState.Conflict)) return;
                    rec.State = RecordingState.Cancelled; rec.FailureReason = "event cancelled/postponed"; rec.UpdatedUtc = now;
                    db.Notifications.Add(new Notification { RecordingId = rid, TsUtc = now, Kind = NotificationKind.Cancelled, Severity = Severity.Info, ToState = "Cancelled", Message = "event cancelled/postponed" });
                    await db.SaveChangesAsync(ct);
                }, ct);
                slots.RemoveAll(s => s.RecordingId == rid);
            }
            // Don't re-process the ones we just cancelled in the conflict re-evaluation below.
            var staleSet = staleRecIds.ToHashSet();
            conflicts = conflicts.Where(c => !staleSet.Contains(c.Id)).ToList();
        }

        // 1c) Revive events whose postponement cleared. The sweep above cancels a recording when its event goes
        // Postponed/Cancelled; the Cancelled row then keeps the event in handledEventIds FOREVER, so if the provider
        // later un-postpones the match it would silently never record again. Reclaim ONLY recordings WE sweep-cancelled
        // (the FailureReason marker) whose event is active again — a user cancellation stays terminal. Delete the dead
        // row + drop the event from handledEventIds so 2b re-places it fresh (current time, full resolution + planning).
        var reviveRows = await (from r in db.Recordings
                                join e in db.Events on r.EventId equals e.Id
                                where r.State == RecordingState.Cancelled && r.FailureReason == "event cancelled/postponed"
                                      && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Live || e.Status == EventStatus.Unknown)
                                select new { r.Id, EventId = r.EventId!.Value }).ToListAsync(ct);
        if (reviveRows.Count > 0)
        {
            var reviveIds = reviveRows.Select(x => x.Id).ToList();
            await _gate.WriteAsync(async () =>
            {
                await db.RecordingFallbacks.Where(f => reviveIds.Contains(f.RecordingId)).ExecuteDeleteAsync(ct);
                await db.Recordings.Where(r => reviveIds.Contains(r.Id) && r.State == RecordingState.Cancelled).ExecuteDeleteAsync(ct);
            }, ct);
            foreach (var x in reviveRows) handledEventIds.Remove(x.EventId);
            _log.LogInformation("[AutoSchedule] revived {N} event(s) whose recording was cancelled by a now-cleared postponement", reviveRows.Count);
        }

        // 2a) Re-evaluate parked conflicts FIRST (they have been waiting): a freed slot promotes one back to Pending.
        foreach (var r in conflicts.OrderBy(r => r.StartUtc))
        {
            try
            {
                if (r.EventId is not { } eid) continue; // only event-linked conflicts are auto-replanned
                // Reconcile to the event's CURRENT time before planning AND before arming. The event may have moved on
                // re-sync while this recording sat parked (the row keeps the old time); promoting on the stale row time
                // would arm the capture at the wrong window. Recompute the window from the live event.
                var ev = await db.Events.FindAsync(new object?[] { eid }, ct);
                if (ev is null) continue;
                var cSport = monitoredLeagues.TryGetValue(ev.LeagueId, out var cLeague) ? cLeague.Sport : "";
                var newEndC = ev.EndUtc ?? ev.StartUtc + await settings.GetEventDurationSecondsAsync(cSport, cLeague?.EventDurationOverrideS, cLeague?.SessionDurationsJson, ev.Title);
                var winStartC = ev.StartUtc - r.PrePadS;
                var winEndC = newEndC + r.PostPadS;
                if (now >= winEndC) { await MarkConflictMissedAsync(db, r.Id, now); continue; } // window passed while parked

                var opts = await planner.OptionsForEventAsync(eid, ct);
                var rank = RankOf(r);
                var decision = planner.Decide(opts, winStartC, winEndC, rank, slots);
                if (!decision.Placed || decision.Option is not { } opt) continue; // still no room → stays Conflict

                await _gate.WriteAsync(async () =>
                {
                    if (decision.PreemptRecordingId is { } vid) await PreemptAsync(vid, $"preempted by higher-priority recording #{r.Id}");
                    var rec = await db.Recordings.FindAsync(r.Id);
                    if (rec is null) return;
                    // SourceId is part of the (Id, SourceId) alternate key — re-point via RecordingRepoint (delete
                    // fallbacks + tracker-bypassing UPDATE of source/channel/stream). Mutating the tracked entity here
                    // would throw "part of a key cannot be modified" when a parked conflict is promoted to a DIFFERENT
                    // credential, and the old flow also deleted fallbacks AFTER persisting the SourceId change (wrong FK
                    // order). The fields below are non-key; WriteFallbacksAsync re-adds the new same-credential ladder.
                    await RecordingRepoint.ApplyAsync(db, r.Id, opt.SourceId, opt.ChannelId, opt.StreamId, now);
                    rec.State = RecordingState.Pending;
                    rec.StartUtc = ev.StartUtc; rec.EndUtc = newEndC; rec.Title = ev.Title; // retime/retitle to the live event so the capture arms on the real window
                    rec.FailureReason = null; rec.UpdatedUtc = now;
                    db.Notifications.Add(new Notification { RecordingId = r.Id, TsUtc = now, Kind = NotificationKind.Conflict, Severity = Severity.Info, ToState = "Pending", Message = $"credential freed → scheduled on '{opt.ChannelName}'" });
                    // The helper already committed the new SourceId, so the new fallbacks' (RecordingId, SourceId) FK is
                    // satisfied; one SaveChanges now persists the non-key fields + the new ladder + the notification.
                    await WriteFallbacksAsync(r.Id, opt.SourceId, opt.Fallbacks);
                    await db.SaveChangesAsync(ct);
                }, ct);
                slots.Add(new CreditAwarePlanner.Slot(opt.SourceId, winStartC, winEndC, r.Id, RecordingState.Pending, rank));
                promoted++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] failed to re-evaluate conflict recording {Id}", r.Id); }
        }

        // 2b) Place new monitored events.
        foreach (var e in candidates)
        {
            // Per-candidate isolation: a single un-resolvable / write-failing event must never abort the whole
            // tick (which would silently stop arming every later event). Log and move on.
            try
            {
                var l = monitoredLeagues[e.LeagueId];
                if (e.StartUtc > now + (long)l.ScheduleHorizonDays * 86400) continue; // beyond this league's horizon
                if (handledEventIds.Contains(e.Id))
                {
                    // Already has a recording — but if the event MOVED/retitled on re-sync, retime its Pending recording
                    // (the auto-scheduler is the only place this reconciliation happens; the scheduler arms off the
                    // recording row, not the event). Only Pending is touched; channel re-resolution is left as-is.
                    if (pendingByEvent.TryGetValue(e.Id, out var pend))
                    {
                        var newEnd = e.EndUtc ?? e.StartUtc + await settings.GetEventDurationSecondsAsync(l.Sport, l.EventDurationOverrideS, l.SessionDurationsJson, e.Title);
                        if (pend.StartUtc != e.StartUtc || pend.EndUtc != newEnd || pend.Title != e.Title)
                        {
                            await _gate.WriteAsync(async () =>
                            {
                                var rec = await db.Recordings.FindAsync(pend.Id);
                                if (rec is not null && rec.State == RecordingState.Pending)
                                {
                                    rec.StartUtc = e.StartUtc; rec.EndUtc = newEnd; rec.Title = e.Title; rec.UpdatedUtc = now;
                                    await db.SaveChangesAsync(ct);
                                }
                            }, ct);
                            _log.LogInformation("[AutoSchedule] reconciled recording {Id} to moved event {Eid} '{Title}'", pend.Id, e.Id, e.Title);
                            // Keep the in-memory planner slot in sync so a later candidate THIS tick is planned against
                            // the new window — otherwise it compares against the stale slot and can double-book the login.
                            pend.StartUtc = e.StartUtc; pend.EndUtc = newEnd; pend.Title = e.Title;
                            slots.RemoveAll(s => s.RecordingId == pend.Id);
                            slots.Add(new CreditAwarePlanner.Slot(pend.SourceId, e.StartUtc - pend.PrePadS, newEnd + pend.PostPadS, pend.Id, RecordingState.Pending, RankOf(pend)));
                        }
                    }
                    continue;
                }

                var opts = await planner.OptionsForEventAsync(e.Id, ct);
                if (opts.Count == 0) { _log.LogDebug("[AutoSchedule] event {Id} '{Title}' not resolvable", e.Id, e.Title); continue; }

                // Defensive: events are given a per-sport EndUtc at ingest, but a legacy/manual row may have none.
                var endUtc = e.EndUtc ?? e.StartUtc + await settings.GetEventDurationSecondsAsync(l.Sport, l.EventDurationOverrideS, l.SessionDurationsJson, e.Title);
                var winStart = e.StartUtc - pre; var winEnd = endUtc + post;
                var rank = CreditAwarePlanner.MakeRank(RecordingPriority.Normal, l.Priority, e.StartUtc, e.Id);
                var decision = planner.Decide(opts, winStart, winEnd, rank, slots);
                var chosen = decision.Option ?? opts[0]; // for a conflict, file it against the primary credential for display

                var snapshot = JsonSerializer.Serialize(new
                {
                    resolved_channel_id = chosen.ChannelId,
                    channel = chosen.ChannelName,
                    spread = decision.Option is not null && decision.Option.SourceId != opts[0].SourceId,
                    resolver_version = 2,
                    resolved_at = now,
                    fallbacks = chosen.Fallbacks.Select(f => f.channelId).ToArray(),
                });

                var newId = 0;
                await _gate.WriteAsync(async () =>
                {
                    if (decision.PreemptRecordingId is { } vid) await PreemptAsync(vid, $"preempted by higher-priority '{e.Title}'");
                    var rec = new RecordingEntity
                    {
                        EventId = e.Id, SourceId = chosen.SourceId, ChannelId = chosen.ChannelId, StreamId = chosen.StreamId,
                        StartUtc = e.StartUtc, EndUtc = endUtc,
                        PrePadS = pre, PostPadS = post, Title = e.Title, Priority = RecordingPriority.Normal,
                        State = decision.Placed ? RecordingState.Pending : RecordingState.Conflict,
                        FailureReason = decision.Placed ? null : decision.Reason,
                        ResolutionSnapshotJson = snapshot, CreatedUtc = now, UpdatedUtc = now,
                    };
                    db.Recordings.Add(rec);
                    await db.SaveChangesAsync(ct);
                    newId = rec.Id;
                    if (decision.Placed)
                        await WriteFallbacksAsync(rec.Id, chosen.SourceId, chosen.Fallbacks);
                    else
                        db.Notifications.Add(new Notification { RecordingId = rec.Id, TsUtc = now, Kind = NotificationKind.Conflict, Severity = Severity.Warn, ToState = "Conflict", Message = decision.Reason });
                    await db.SaveChangesAsync(ct);
                }, ct);

                if (decision.Placed)
                {
                    slots.Add(new CreditAwarePlanner.Slot(chosen.SourceId, winStart, winEnd, newId, RecordingState.Pending, rank));
                    placed++;
                }
                else conflicted++;
                handledEventIds.Add(e.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] failed to schedule event {Id} '{Title}'", e.Id, e.Title); }
        }
        if (placed > 0 || conflicted > 0 || promoted > 0)
            _log.LogInformation("[AutoSchedule] placed {P}, promoted {Pr}, conflicted {C} (across {N} credential(s))", placed, promoted, conflicted, slots.Select(s => s.SourceId).Distinct().Count());
        return interval;
    }

    /// <summary>A parked conflict whose whole window elapsed (both logins stayed busy throughout) → terminal Missed.</summary>
    private async Task MarkConflictMissedAsync(DVarrDbContext db, int recId, long now)
    {
        await _gate.WriteAsync(async () =>
        {
            var r = await db.Recordings.FindAsync(recId);
            if (r is null || r.State != RecordingState.Conflict) return;
            r.State = RecordingState.Missed; r.UpdatedUtc = now;
            r.FailureReason = "conflict window elapsed (both logins busy throughout)";
            db.Notifications.Add(new Notification { RecordingId = recId, TsUtc = now, Kind = NotificationKind.Missed, Severity = Severity.Critical, ToState = "Missed", Message = r.FailureReason });
            await db.SaveChangesAsync();
        });
    }
}
