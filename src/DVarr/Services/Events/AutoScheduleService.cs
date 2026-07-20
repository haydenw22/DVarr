using System.Text.Json;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services;
using Microsoft.EntityFrameworkCore;
using RecordingEntity = DVarr.Data.Entities.Recording;

namespace DVarr.Services.Events;

/// <summary>
/// The P1 league/event auto-record layer: periodically (a) refreshes events for monitored leagues
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

    // Per-league stamp of the last "no channel mapping" warning (≤1 per league per day; static — service is a singleton
    // hosted service but keep it instance-independent for safety).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _lastNoMapWarn = new();

    // Last operational-data retention prune (audit DB-01): Notifications and ScheduleTicks are append-only and grew
    // forever; everything else high-frequency is already bounded (Programmes swap per sync, RecordingSegments delete
    // per finalize). Runs at most once a day, piggybacked on the schedule tick.
    private static long _lastRetentionPruneUtc;

    // Last low-disk-space warning: warn at most once / 6h so a genuinely full disk nags without spamming the feed.
    private static long _lastLowDiskWarnUtc;

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
        => ParseMonitoredTeamIdList(json).ToHashSet(StringComparer.Ordinal);

    /// <summary>Ordered variant of <see cref="ParseMonitoredTeamIds"/>: the stored list order IS the user's team
    /// recording priority (first = most important — see the Leagues page priority dialog), so the conflict ladder
    /// needs the sequence, not just the set.</summary>
    public static List<string> ParseMonitoredTeamIdList(string? json)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return list;
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
                    // trim to match SerializeTeams (stored trimmed); dedupe so a doubled id can't skew priority scores
                    if (!string.IsNullOrWhiteSpace(id) && !list.Contains(id!.Trim())) list.Add(id!.Trim());
                }
        }
        catch { /* malformed → treat as "all teams" */ }
        return list;
    }

    /// <summary>Team-priority score for the conflict ladder: the higher-priority side of the event, scored so the
    /// FIRST team in the league's followed list gets the biggest number (n), the last gets 1, and an event whose
    /// teams aren't in the list (or a league with no list) gets 0.</summary>
    public static int TeamPriorityScore(List<string>? orderedTeamIds, string? homeTeamId, string? awayTeamId)
    {
        if (orderedTeamIds is null || orderedTeamIds.Count == 0) return 0;
        var best = 0;
        if (homeTeamId is not null) { var i = orderedTeamIds.IndexOf(homeTeamId); if (i >= 0) best = Math.Max(best, orderedTeamIds.Count - i); }
        if (awayTeamId is not null) { var i = orderedTeamIds.IndexOf(awayTeamId); if (i >= 0) best = Math.Max(best, orderedTeamIds.Count - i); }
        return best;
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
        // Pre/post-roll padding is resolved PER-SPORT at the creation site below (GetPadsForSportAsync): each sport's
        // profile pads apply, an explicit 0 survives (audit SET-02), and an unset field inherits the global default.

        // 0) Daily retention prune (audit DB-01): keep 30 days of Notifications (the Activity feed) and 7 days of
        //    ScheduleTicks (per-tick diagnostics). Both are pure observability data — nothing joins them.
        if (now - Interlocked.Read(ref _lastRetentionPruneUtc) > 86400)
        {
            Interlocked.Exchange(ref _lastRetentionPruneUtc, now);
            try
            {
                await _gate.WriteAsync(async () =>
                {
                    var n = await db.Notifications.Where(x => x.TsUtc < now - 30L * 86400).ExecuteDeleteAsync(ct);
                    var t = await db.ScheduleTicks.Where(x => x.TickUtc < now - 7L * 86400).ExecuteDeleteAsync(ct);
                    if (n > 0 || t > 0) _log.LogInformation("[AutoSchedule] retention prune: {N} notification(s), {T} schedule tick(s) removed", n, t);
                }, ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] retention prune failed (will retry tomorrow)"); }
        }

        // 0b) Low-space guardrail: warn (≤ once / 6h) when a media/segments filesystem is already under its floor, so a
        //     full disk surfaces as a loud alert well before a 2am recording fails on write.
        try
        {
            var mediaFloor = await settings.GetIntAsync("disk_min_free_gb") * 1_000_000_000L;
            var segFloor = await settings.GetIntAsync("disk_min_free_segments_gb") * 1_000_000_000L;
            if ((mediaFloor > 0 || segFloor > 0) && now - Interlocked.Read(ref _lastLowDiskWarnUtc) > 6 * 3600)
            {
                var paths = scope.ServiceProvider.GetRequiredService<RuntimePaths>();
                var low = DiskGuard.CurrentLowSpace(paths, mediaFloor, segFloor);
                if (low.Count > 0)
                {
                    Interlocked.Exchange(ref _lastLowDiskWarnUtc, now);
                    var msg = "Low disk space — " + string.Join("; ", low);
                    await _gate.WriteAsync(async () =>
                    {
                        db.Notifications.Add(new Notification { TsUtc = now, Kind = NotificationKind.LowDiskSpace, Severity = Severity.Warn, Message = msg });
                        await db.SaveChangesAsync(ct);
                    }, ct);
                    _log.LogWarning("[AutoSchedule] {Msg}", msg);
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] low-space check failed"); }

        // 0c) Retention sweep: evict old finished recordings per each league's policy (unprotected-oldest first,
        //     reusing the safe delete-with-files primitive). No-op unless a non-keep_all policy is configured. Fires
        //     once per LOCAL day (the Display timezone) at retention_sweep_time; watched games past their safety
        //     window are swept here too, as a daily backstop to the per-tick check in 0d. The stamp advances only on
        //     SUCCESS (audit RET-05) — a failed sweep (locked db, unmounted drive) retries next tick, not tomorrow.
        try
        {
            var localNow = EpochTime.ToDisplay(now);
            var todayLocal = localNow.ToString("yyyy-MM-dd");
            var lastFired = (await settings.GetAsync("retention_sweep_last") ?? "").Trim();
            var sweepTime = (await settings.GetAsync("retention_sweep_time") ?? "03:00").Trim();
            if (lastFired != todayLocal && TimeSpan.TryParse(sweepTime, out var sched) && localNow.TimeOfDay >= sched)
            {
                var (n, freed, _) = await scope.ServiceProvider.GetRequiredService<DVarr.Services.Media.RetentionService>().SweepAsync(ct: ct);
                await settings.SetAsync("retention_sweep_last", todayLocal, ct); // stamp on success only — a throw retries next tick
                if (n > 0) _log.LogInformation("[AutoSchedule] retention evicted {N} item(s), freed {Gb:0.0} GB", n, freed / 1_000_000_000.0);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] retention sweep failed (retrying next tick after the scheduled time)"); }

        // 0d) Delete-after-watched: remove watched games whose safety window (estimated finish + buffer) has passed.
        //     Checked EVERY tick so a finished game clears within minutes — but never before the viewer would have
        //     reached the end (the media server flags watched at a ~90% position, not true end-of-play).
        try
        {
            var n = await scope.ServiceProvider.GetRequiredService<DVarr.Services.Media.RetentionService>().EvictWatchedDueAsync(ct);
            if (n > 0) _log.LogInformation("[AutoSchedule] delete-after-watched removed {N} game(s) past their safety window", n);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] watched-delete check failed"); }

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

        // Generous SQL cap only (bounded memory) — the ELIGIBILITY cap is applied AFTER the team/session filters
        // below (audit SCH-01: capping before the filters let 500 early non-followed events starve later valid
        // ones). 5000 rows is far beyond any real 60-day monitored-league window; log if it's ever hit.
        var candidates = await db.Events.AsNoTracking()
            .Where(e => e.Monitored && monitoredLeagues.Keys.Contains(e.LeagueId)
                        && e.StartUtc > now - 3600 && e.StartUtc < maxLookahead
                        && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Live || e.Status == EventStatus.Unknown))
            .OrderBy(e => e.StartUtc).Take(5000).ToListAsync(ct);
        if (candidates.Count == 5000)
            _log.LogWarning("[AutoSchedule] candidate query hit the 5000-row safety cap — events beyond it are deferred to later ticks");

        // Team-follow: a league with MonitoredTeamsJson set records ONLY those teams' matches. (The full schedule is
        // still ingested so episode numbers stay correct — this filters only what the scheduler arms.) Keep an event
        // if its league follows all teams (no list) OR either side's team id is in the followed set. A revived event
        // (1c) re-enters this same candidate pool, so it's covered without a separate guard.
        var followedTeams = monitoredLeagues.Values
            .Select(l => (l.Id, Ids: ParseMonitoredTeamIds(l.MonitoredTeamsJson)))
            .Where(x => x.Ids.Count > 0).ToDictionary(x => x.Id, x => x.Ids);
        if (followedTeams.Count > 0)
            candidates = candidates.Where(e =>
                // A MANUALLY armed event (MonitoredLocked; Monitored is guaranteed by the pool query) always records,
                // exactly like the calendar's follow filter (LeagueEndpoints.EventFollowed) — without this, a user
                // could arm a non-followed team's match, see it "monitored" on the calendar, and never get a recording.
                e.MonitoredLocked
                || !followedTeams.TryGetValue(e.LeagueId, out var ids)
                || (e.HomeTeamId != null && ids.Contains(e.HomeTeamId))
                || (e.AwayTeamId != null && ids.Contains(e.AwayTeamId))
                // Fail OPEN for an event with NO team ids at all (e.g. a not-yet-drawn final, or pre-v1.19 data): we
                // can't tell who's playing, so don't silently drop it — better to over-record than miss a real match.
                || (e.HomeTeamId == null && e.AwayTeamId == null)).ToList();

        // Session-follow (motorsport ONLY — guarded by sport, since every non-motorsport title classifies as "Race" and
        // an API-set session list on a team-sport league would silently drop all its matches): a league with
        // MonitoredSessionsJson arms only those session kinds. Keep an event if its league follows all sessions, OR its
        // title classifies to a followed kind (fail-open if unclassifiable).
        var followedSessions = monitoredLeagues.Values
            .Where(l => MotorsportSession.IsMotorsport(l.Sport))
            .Select(l => (l.Id, Kinds: ParseMonitoredSessions(l.MonitoredSessionsJson)))
            .Where(x => x.Kinds.Count > 0).ToDictionary(x => x.Id, x => x.Kinds);
        if (followedSessions.Count > 0)
            candidates = candidates.Where(e =>
                e.MonitoredLocked // manual arm always records (mirrors the team filter + the calendar's EventFollowed)
                || !followedSessions.TryGetValue(e.LeagueId, out var kinds)
                || MotorsportSession.Classify(e.Title) is not { } k
                || kinds.Contains(k)).ToList();

        // Per-tick work cap on the ELIGIBLE set (soonest first — the list is StartUtc-ordered). Applied after the
        // filters so followed events can never be starved by early non-followed ones (audit SCH-01); anything past
        // the cap is simply picked up by a later tick.
        if (candidates.Count > 500) candidates = candidates.Take(500).ToList();

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

        // League + team priority for ranking existing recordings (their event's league/teams) — for the conflict ladder.
        var rankEventIds = committed.Concat(conflicts).Where(r => r.EventId != null).Select(r => r.EventId!.Value).Distinct().ToList();
        var evInfo = await db.Events.Where(e => rankEventIds.Contains(e.Id))
            .Select(e => new { e.Id, e.LeagueId, e.HomeTeamId, e.AwayTeamId })
            .ToDictionaryAsync(e => e.Id, ct);
        var leagueRank = await db.Leagues.Select(l => new { l.Id, l.Priority, l.MonitoredTeamsJson }).ToListAsync(ct);
        var leaguePrio = leagueRank.ToDictionary(l => l.Id, l => l.Priority);
        // Followed-team ORDER per league (first = highest team priority) — parsed once per tick.
        var leagueTeamOrder = leagueRank.ToDictionary(l => l.Id, l => ParseMonitoredTeamIdList(l.MonitoredTeamsJson));
        int LeaguePrioOf(int? eventId) => eventId is { } eid && evInfo.TryGetValue(eid, out var x) && leaguePrio.TryGetValue(x.LeagueId, out var p) ? p : 0;
        int TeamPrioOf(int? eventId) => eventId is { } eid && evInfo.TryGetValue(eid, out var x) && leagueTeamOrder.TryGetValue(x.LeagueId, out var order)
            ? TeamPriorityScore(order, x.HomeTeamId, x.AwayTeamId) : 0;
        CreditAwarePlanner.PRank RankOf(RecordingEntity r) => CreditAwarePlanner.MakeRank(r.Priority, LeaguePrioOf(r.EventId), TeamPrioOf(r.EventId), r.StartUtc, r.Id);

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
        // GUARDED by candidateIds, exactly as 1d is: 2b only ever iterates candidates, so deleting the row for an event
        // that has fallen out of the candidate pool (e.g. it started more than an hour ago) evaporates the recording
        // entirely — no row, no conflict, no notification, nothing to re-place it. Leave those alone.
        var candidateIds = candidates.Select(x => x.Id).ToHashSet();
        var reviveRows = await (from r in db.Recordings
                                join e in db.Events on r.EventId equals e.Id
                                where r.State == RecordingState.Cancelled && r.FailureReason == "event cancelled/postponed"
                                      && (e.Status == EventStatus.Scheduled || e.Status == EventStatus.Live || e.Status == EventStatus.Unknown)
                                      && candidateIds.Contains(e.Id)
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

        // 1d) Revive events re-included by a WIDENED team/session follow filter. Narrowing the filter sweep-cancels
        // Pending/Conflict recordings (LeagueEndpoints PUT, the FailureReason markers below); that Cancelled row then
        // keeps the event in handledEventIds forever, so removing a team and later re-adding it would silently never
        // record its events again. Mirror 1c: reclaim ONLY filter-sweep-cancelled rows whose event is back in TODAY'S
        // candidate pool (it passes the current filters), delete the dead row and drop the event from handledEventIds
        // so 2b re-places it fresh. A user's own cancellation carries a different/absent reason and stays terminal.
        if (candidateIds.Count > 0)
        {
            var filterRevive = await db.Recordings.AsNoTracking()
                .Where(r => r.State == RecordingState.Cancelled && r.EventId != null && candidateIds.Contains(r.EventId.Value)
                            && (r.FailureReason == "team removed from team-follow filter"
                                || r.FailureReason == "session removed from session-follow filter"))
                .Select(r => new { r.Id, EventId = r.EventId!.Value }).ToListAsync(ct);
            if (filterRevive.Count > 0)
            {
                var ids = filterRevive.Select(x => x.Id).ToList();
                await _gate.WriteAsync(async () =>
                {
                    await db.RecordingFallbacks.Where(f => ids.Contains(f.RecordingId)).ExecuteDeleteAsync(ct);
                    await db.Recordings.Where(r => ids.Contains(r.Id) && r.State == RecordingState.Cancelled).ExecuteDeleteAsync(ct);
                }, ct);
                foreach (var x in filterRevive) handledEventIds.Remove(x.EventId);
                _log.LogInformation("[AutoSchedule] revived {N} event(s) re-included by a widened team/session follow filter", filterRevive.Count);
            }
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
                    if (decision.PreemptRecordingId is { } vid) await PreemptAsync(vid, $"preempted by recording #{r.Id} (won on {decision.PreemptWhy})");
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
                            var newWinStart = e.StartUtc - pend.PrePadS; var newWinEnd = newEnd + pend.PostPadS;
                            // Drop pend's OWN (stale) slot before testing overlap, so it can't self-collide, then check
                            // whether the NEW padded window now clashes with another slot-holding recording on the SAME
                            // credential. A blind retime-in-place would double-book that credential's single stream — at
                            // arm time only one could lease it, marking the other Missed. Reuse the SAME placement path as
                            // 2b (OptionsForEventAsync + Decide) so a clash re-homes to a free equivalent login or parks
                            // as Conflict for a later tick, never a silent same-credential double-book.
                            slots.RemoveAll(s => s.RecordingId == pend.Id);
                            var sameCredClash = slots.Any(s => s.SourceId == pend.SourceId && CreditAwarePlanner.Overlaps(newWinStart, newWinEnd, s.StartUtc, s.EndUtc));

                            if (!sameCredClash)
                            {
                                // No overlap → retime in place (unchanged behavior).
                                await _gate.WriteAsync(async () =>
                                {
                                    var rec = await db.Recordings.FindAsync(pend.Id);
                                    if (rec is not null && rec.State == RecordingState.Pending)
                                    {
                                        var movedS = Math.Abs(e.StartUtc - rec.StartUtc);
                                        rec.StartUtc = e.StartUtc; rec.EndUtc = newEnd; rec.Title = e.Title; rec.UpdatedUtc = now;
                                        // A silently re-aimed recording looks identical to one that never existed — and a
                                        // provider moving an event to a bogus/placeholder time (endemic to doubleheaders)
                                        // walks the recording off the real broadcast. Surface any REAL window shift in the
                                        // Activity feed; the clash branch below already notifies for its own case.
                                        if (movedS >= 300)
                                            db.Notifications.Add(new Notification
                                            {
                                                RecordingId = pend.Id, TsUtc = now, Kind = NotificationKind.Retimed, Severity = Severity.Info, ToState = "Pending",
                                                Message = $"provider moved this event by {movedS / 60}m — recording retimed to match",
                                            });
                                        await db.SaveChangesAsync(ct);
                                    }
                                }, ct);
                                _log.LogInformation("[AutoSchedule] reconciled recording {Id} to moved event {Eid} '{Title}'", pend.Id, e.Id, e.Title);
                                // Keep the in-memory planner slot in sync so a later candidate THIS tick is planned against
                                // the new window — otherwise it compares against the stale slot and can double-book the login.
                                pend.StartUtc = e.StartUtc; pend.EndUtc = newEnd; pend.Title = e.Title;
                                slots.Add(new CreditAwarePlanner.Slot(pend.SourceId, newWinStart, newWinEnd, pend.Id, RecordingState.Pending, RankOf(pend)));
                            }
                            else
                            {
                                // The moved window collides with another committed recording on this credential → re-plan
                                // it exactly like a new placement. Apply the new times in-memory FIRST so RankOf/Decide use
                                // the real (moved) window; if placement fails the row stays Pending on disk until we park it.
                                pend.StartUtc = e.StartUtc; pend.EndUtc = newEnd; pend.Title = e.Title;
                                var moveOpts = await planner.OptionsForEventAsync(e.Id, ct);
                                var moveRank = RankOf(pend);
                                var moveDecision = moveOpts.Count == 0
                                    ? new CreditAwarePlanner.Decision(false, null, null, true, "no resolvable channel for moved event")
                                    : planner.Decide(moveOpts, newWinStart, newWinEnd, moveRank, slots);
                                if (moveDecision.Placed && moveDecision.Option is { } opt)
                                {
                                    await _gate.WriteAsync(async () =>
                                    {
                                        if (moveDecision.PreemptRecordingId is { } vid) await PreemptAsync(vid, $"preempted by moved recording #{pend.Id}");
                                        var rec = await db.Recordings.FindAsync(pend.Id);
                                        if (rec is null || rec.State != RecordingState.Pending) return;
                                        // SourceId is part of the (Id, SourceId) alternate key — re-point via RecordingRepoint
                                        // (delete fallbacks + tracker-bypassing UPDATE) BEFORE mutating non-key fields, then
                                        // rebuild the same-credential ladder (WriteFallbacksAsync). Same as the 2a promotion.
                                        await RecordingRepoint.ApplyAsync(db, pend.Id, opt.SourceId, opt.ChannelId, opt.StreamId, now);
                                        rec.StartUtc = e.StartUtc; rec.EndUtc = newEnd; rec.Title = e.Title; rec.UpdatedUtc = now;
                                        await WriteFallbacksAsync(pend.Id, opt.SourceId, opt.Fallbacks);
                                        db.Notifications.Add(new Notification { RecordingId = pend.Id, TsUtc = now, Kind = NotificationKind.Conflict, Severity = Severity.Info, ToState = "Pending", Message = $"moved event overlapped its credential → rescheduled on '{opt.ChannelName}'" });
                                        await db.SaveChangesAsync(ct);
                                    }, ct);
                                    _log.LogInformation("[AutoSchedule] reconciled recording {Id} to moved event {Eid} '{Title}' and re-homed it (moved window overlapped its credential)", pend.Id, e.Id, e.Title);
                                    pend.SourceId = opt.SourceId;
                                    slots.Add(new CreditAwarePlanner.Slot(opt.SourceId, newWinStart, newWinEnd, pend.Id, RecordingState.Pending, moveRank));
                                }
                                else
                                {
                                    // Nowhere free this tick → retime AND park in Conflict (releasing the slot). The row now
                                    // carries the correct moved window, so 2a's conflict-promotion path re-homes it next tick
                                    // (it re-reads Conflicts fresh, reconciles to the live event time, and re-runs Decide).
                                    await _gate.WriteAsync(async () =>
                                    {
                                        var rec = await db.Recordings.FindAsync(pend.Id);
                                        if (rec is null || rec.State != RecordingState.Pending) return;
                                        rec.StartUtc = e.StartUtc; rec.EndUtc = newEnd; rec.Title = e.Title;
                                        rec.State = RecordingState.Conflict; rec.FailureReason = moveDecision.Reason; rec.UpdatedUtc = now;
                                        db.Notifications.Add(new Notification { RecordingId = pend.Id, TsUtc = now, Kind = NotificationKind.Conflict, Severity = Severity.Warn, ToState = "Conflict", Message = $"moved event overlapped its credential — {moveDecision.Reason}" });
                                        await db.SaveChangesAsync(ct);
                                    }, ct);
                                    _log.LogWarning("[AutoSchedule] moved recording {Id} (event {Eid} '{Title}') overlapped its credential and no login was free → parked Conflict", pend.Id, e.Id, e.Title);
                                    // Slot already removed above; leaving it out releases the credential for this tick.
                                }
                            }
                        }
                    }
                    continue;
                }

                var opts = await planner.OptionsForEventAsync(e.Id, ct);
                if (opts.Count == 0)
                {
                    // Information, not Debug: this is a SILENT recording loss and the in-app log viewer only keeps
                    // Information and above, so at Debug the one line that explains "why didn't my game record?" was
                    // invisible to every user. The Activity notification below is throttled per league per day; this
                    // line is per event, so the log always shows exactly which fixtures were skipped.
                    _log.LogInformation("[AutoSchedule] event {Id} '{Title}' NOT SCHEDULED — no channel mapping resolves it (check the league's mappings, and that the event still has its teams)", e.Id, e.Title);
                    // A monitored event that CAN'T be scheduled is a silent recording loss (tonight's Crows game) —
                    // surface it in the Activity feed, at most once per league per day so 20 fixtures don't spam.
                    if (_lastNoMapWarn.GetOrAdd(e.LeagueId, 0) is var lastWarn && now - lastWarn > 86400 && _lastNoMapWarn.TryUpdate(e.LeagueId, now, lastWarn))
                        await _gate.WriteAsync(async () =>
                        {
                            db.Notifications.Add(new Notification
                            {
                                TsUtc = now, Kind = NotificationKind.Unresolvable, Severity = Severity.Warn,
                                Message = $"League '{l.Name}': monitored event '{e.Title}' can't be scheduled — no usable channel mapping. Map a channel on the Leagues page.",
                            });
                            await db.SaveChangesAsync(ct);
                        }, ct);
                    continue;
                }

                // Defensive: events are given a per-sport EndUtc at ingest, but a legacy/manual row may have none.
                var endUtc = e.EndUtc ?? e.StartUtc + await settings.GetEventDurationSecondsAsync(l.Sport, l.EventDurationOverrideS, l.SessionDurationsJson, e.Title);
                var (pre, post) = await settings.GetPadsForSportAsync(l.Sport); // per-sport pre/post-roll (inherits the global default)
                var winStart = e.StartUtc - pre; var winEnd = endUtc + post;
                var rank = CreditAwarePlanner.MakeRank(RecordingPriority.Normal, l.Priority,
                    TeamPriorityScore(leagueTeamOrder.GetValueOrDefault(e.LeagueId), e.HomeTeamId, e.AwayTeamId), e.StartUtc, e.Id);
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
                    if (decision.PreemptRecordingId is { } vid) await PreemptAsync(vid, $"preempted by '{e.Title}' (won on {decision.PreemptWhy})");
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
                    // Disk guardrail (warn-only) for the hands-off path too (audit DISK-01): the manual scheduling
                    // endpoint already warns when a recording is projected to breach the free-space floor — an
                    // auto-armed one must not be quieter about the same risk. Never blocks a placement.
                    try
                    {
                        var mediaFloor = await settings.GetIntAsync("disk_min_free_gb") * 1_000_000_000L;
                        var segFloor = await settings.GetIntAsync("disk_min_free_segments_gb") * 1_000_000_000L;
                        if (mediaFloor > 0 || segFloor > 0)
                        {
                            var paths = scope.ServiceProvider.GetRequiredService<RuntimePaths>();
                            var ch = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chosen.ChannelId, ct);
                            var projected = await DiskGuard.ProjectBytesAsync(db, chosen.ChannelId, ch?.DetectedQuality, winEnd - winStart, ct);
                            var warns = DiskGuard.ProjectedWarnings(projected, paths, mediaFloor, segFloor);
                            if (warns.Count > 0)
                                await _gate.WriteAsync(async () =>
                                {
                                    db.Notifications.Add(new Notification
                                    {
                                        RecordingId = newId, TsUtc = now, Kind = NotificationKind.LowDiskSpace, Severity = Severity.Warn,
                                        Message = $"'{e.Title}' may run the disk low — " + string.Join("; ", warns),
                                    });
                                    await db.SaveChangesAsync(ct);
                                }, ct);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _log.LogDebug(ex, "[AutoSchedule] disk projection failed for '{Title}'", e.Title); }
                }
                else conflicted++;
                handledEventIds.Add(e.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] failed to schedule event {Id} '{Title}'", e.Id, e.Title); }
        }
        if (placed > 0 || conflicted > 0 || promoted > 0)
            _log.LogInformation("[AutoSchedule] placed {P}, promoted {Pr}, conflicted {C} (across {N} credential(s))", placed, promoted, conflicted, slots.Select(s => s.SourceId).Distinct().Count());

        // 3) Guide re-pick sweep: any Pending recording starting within 48h (issue #9 — a 1h window meant the
        //    Scheduled list showed the rank-order placement channel all week, so per-game channel corrections were
        //    invisible until an hour before air even when the provider's guide already listed the game days out).
        //    Re-resolve each against the LIVE guide and move to the mapped channel that actually shows the event
        //    (same credential only; EpgRepickService applies threshold + hysteresis + ChannelLocked + kill-switch,
        //    triggers a stale-EPG refresh when this source's guide is >12h old, and only chases a BLANK guide within
        //    1h of start — a guide with no data days out is normal, not a fault). A recording just moves back if a
        //    later guide update contradicts an early move; hysteresis stops tick-to-tick flapping.
        var repick = scope.ServiceProvider.GetRequiredService<EpgRepickService>();
        var repickIds = await db.Recordings.AsNoTracking()
            .Where(r => r.State == RecordingState.Pending && r.EventId != null && !r.ChannelLocked
                        && r.StartUtc > now && r.StartUtc <= now + 48 * 3600)
            .Select(r => r.Id).ToListAsync(ct);
        var repicked = 0;
        foreach (var rid in repickIds)
        {
            try { if (await repick.TryRepickAsync(rid, ct)) repicked++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoSchedule] EPG re-pick failed for recording {Id}", rid); }
        }
        if (repicked > 0) _log.LogInformation("[AutoSchedule] EPG re-pick moved {N} recording(s) to guide-matched channels", repicked);
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

        try
        {
            using var rescueScope = _scopes.CreateScope();
            await RescueService.TryOpenTicketAsync(
                rescueScope.ServiceProvider.GetRequiredService<DVarrDbContext>(), _gate,
                rescueScope.ServiceProvider.GetRequiredService<SettingsService>(),
                recId, "conflict window elapsed (both logins busy)", _log);
        }
        catch (Exception ex) { _log.LogDebug(ex, "[AutoSchedule] rescue-ticket open failed for {Id}", recId); }
    }
}
