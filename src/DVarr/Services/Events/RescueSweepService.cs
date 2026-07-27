using System.Collections.Concurrent;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Ingest;
using Microsoft.EntityFrameworkCore;
using RecordingEntity = DVarr.Data.Entities.Recording;

namespace DVarr.Services.Events;

/// <summary>
/// The second-chance replay hunter. Periodically works the open rescue tickets: settles those whose game now has a
/// good copy, abandons expired ones, and for the rest searches the guide (the league's mapped channels, optionally
/// the whole source) for a full-length re-air airing after the game ended — scheduling the first good match as a
/// low-priority (Opportunistic) replay that never preempts a live recording. Sports re-air constantly, so this
/// turns a failed capture from "lost" into "wait for the repeat".
/// </summary>
public sealed class RescueSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DbWriteGate _gate;
    private readonly Recording.RecorderService _recorder;
    private readonly ILogger<RescueSweepService> _log;

    private const double MinTitleScore = 0.30;         // an EPG programme must actually look like the event
    private const double MinSingleSidedScore = 0.50;   // single-name events (motorsport…) have no both-team gate, so demand a much stronger title match
    private const double AmbiguityMargin = 0.05;       // two DIFFERENT programmes scoring this close is a coin flip — wait for a clearer guide
    private const double MinReplayDurationFrac = 0.70; // ...and be at least this fraction of the game's length (not a highlights show)
    private const int RefreshCooldownS = 30 * 60;      // at most one opportunistic EPG refresh per source per 30 min
    private static readonly ConcurrentDictionary<int, long> _lastRefresh = new();

    public RescueSweepService(IServiceScopeFactory scopes, DbWriteGate gate, Recording.RecorderService recorder, ILogger<RescueSweepService> log)
    { _scopes = scopes; _gate = gate; _recorder = recorder; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken); } catch (OperationCanceledException) { return; }
        _log.LogInformation("[Rescue] Started");
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 900;
            try { interval = await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[Rescue] tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(interval, 60, 3600)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var interval = await settings.GetIntAsync("replay_rescue_interval_s"); if (interval <= 0) interval = 900;
        if (!await settings.GetBoolAsync("replay_rescue_enabled")) return interval;

        var now = EpochTime.Now();
        await SettleTicketsAsync(db, now, ct);

        var due = await db.RescueTickets.AsNoTracking()
            .Where(t => t.State == RescueTicketState.Open && t.NextSweepUtc <= now)
            .OrderBy(t => t.NextSweepUtc).Take(25).Select(t => t.Id).ToListAsync(ct);
        foreach (var tid in due)
        {
            try { await SweepOneAsync(scope, db, tid, interval, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "[Rescue] sweep failed for ticket {Id}", tid); }
        }
        return interval;
    }

    /// <summary>Close tickets whose game now has a good copy; follow up scheduled replays (Done → Closed, failed →
    /// re-open and hunt again); abandon anything past its expiry.</summary>
    private async Task SettleTicketsAsync(DVarrDbContext db, long now, CancellationToken ct)
    {
        var live = await db.RescueTickets.AsNoTracking()
            .Where(t => t.State == RescueTicketState.Open || t.State == RescueTicketState.Scheduled)
            .ToListAsync(ct);
        foreach (var t in live)
        {
            // A good copy landed (the replay finished, or the game got recorded some other way) → close.
            if (await RescueService.HasGoodCopyAsync(db, t.EventId, ct))
            {
                await UpdateTicketAsync(db, t.Id, x => { x.State = RescueTicketState.Closed; x.Note = "a good copy landed"; }, ct);
                // Cancel the not-yet-started replay/catch-up this ticket had scheduled — closing the ticket alone
                // used to ORPHAN it: the pending replay stayed armed, seized the login at air time and re-recorded
                // a game whose good copy (sometimes already watched and deleted) had landed hours earlier. A
                // capture that is already running is left alone (it settles as the good copy itself, or fails and
                // is moot — the ticket is closed either way).
                if (t.State == RescueTicketState.Scheduled && t.ReplayRecordingId is { } orphanId
                    && !_recorder.IsActive(orphanId)) // an arming/armed replay is mid-flight — its supervisor owns the state row
                {
                    var cancelled = false;
                    await _gate.WriteAsync(async () =>
                    {
                        var rep = await db.Recordings.FindAsync(new object?[] { orphanId }, ct);
                        if (rep is not null && rep.State is RecordingState.Pending or RecordingState.Conflict)
                        {
                            rep.State = RecordingState.Cancelled;
                            rep.FailureReason = "rescue closed — a good copy of the game already landed";
                            rep.UpdatedUtc = now;
                            cancelled = true;
                        }
                        await db.SaveChangesAsync(ct);
                    }, ct);
                    if (cancelled)
                        _log.LogInformation("[Rescue] ticket {Id}: cancelled the scheduled replay (recording {Rep}) — a good copy of '{Title}' already landed", t.Id, orphanId, t.Title);
                }
                await NotifyAsync(db, t.RecordingId, NotificationKind.ReplayScheduled, Severity.Info,
                    $"replay rescue closed — a good copy of “{t.Title}” is now in the library", now, ct);
                continue;
            }
            if (t.State == RescueTicketState.Scheduled && t.ReplayRecordingId is { } rid)
            {
                var rep = await db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct);
                // Replay failed / was cancelled → hunt again (unless we're already past the deadline).
                if (rep is null || rep.State is RecordingState.NeedsAttention or RecordingState.Missed or RecordingState.Cancelled)
                {
                    // The USER cancelling the scheduled replay means "stop", not "try again": re-opening the ticket
                    // re-found the same re-air on the next sweep and armed a fresh recording, so the cancel button
                    // appeared to do nothing. Honour it by cancelling the ticket instead.
                    if (rep is { State: RecordingState.Cancelled } && (rep.FailureReason ?? "").Contains("cancelled by user", StringComparison.OrdinalIgnoreCase))
                    {
                        await UpdateTicketAsync(db, t.Id, x => { x.State = RescueTicketState.Cancelled; x.Note = "the scheduled replay was cancelled by the user"; }, ct);
                        _log.LogInformation("[Rescue] ticket {Id}: user cancelled the scheduled replay — hunt stopped", t.Id);
                        continue;
                    }
                    // A live-preempted catch-up pull never contacted the archive — refund its attempt so two busy
                    // evenings can't exhaust the archive path without a single real failure.
                    var refund = rep is { CatchupSourceStartUtc: not null }
                                 && (rep.FailureReason ?? "").Contains("preempted", StringComparison.OrdinalIgnoreCase);
                    if (now > t.ExpiresUtc) await ExpireAsync(db, t, now, ct);
                    else await UpdateTicketAsync(db, t.Id, x =>
                    {
                        x.State = RescueTicketState.Open; x.ReplayRecordingId = null; x.NextSweepUtc = now;
                        if (refund) x.CatchupAttempts = Math.Max(0, x.CatchupAttempts - 1);
                        x.Note = "the scheduled replay failed — hunting again";
                    }, ct);
                }
                continue; // a pending/active replay: leave it to run
            }
            // An open ticket that never found a re-air in time → give up.
            if (t.State == RescueTicketState.Open && now > t.ExpiresUtc) await ExpireAsync(db, t, now, ct);
        }
    }

    private async Task SweepOneAsync(IServiceScope scope, DVarrDbContext db, int ticketId, int interval, CancellationToken ct)
    {
        var now = EpochTime.Now();
        var t = await db.RescueTickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null || t.State != RescueTicketState.Open) return;
        if (now > t.ExpiresUtc) { await ExpireAsync(db, t, now, ct); return; }

        // Candidate channels: the league's mapped channels (optionally every channel on those sources).
        var mapped = await db.LeagueChannelMaps.AsNoTracking().Where(m => m.LeagueId == t.LeagueId)
            .Select(m => m.ChannelId).Distinct().ToListAsync(ct);
        if (mapped.Count == 0) { await BumpAsync(db, t.Id, now + interval, now, "no channels mapped to this league", ct); return; }

        // Disabled SOURCES are off-limits (the recorder refuses to contact them), so scheduling a replay or a
        // catch-up pull there would just sit Pending until Missed — burning the ticket's attempts/deadline on a
        // login that can never serve it.
        var enabledSourceIds = (await db.Sources.AsNoTracking().Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var mappedChans = (await db.Channels.AsNoTracking().Where(c => mapped.Contains(c.Id) && c.Enabled).ToListAsync(ct))
            .Where(c => enabledSourceIds.Contains(c.SourceId)).ToList();
        var sourceIds = mappedChans.Select(c => c.SourceId).Distinct().ToList();
        var candidates = t.WholeSource
            ? await db.Channels.AsNoTracking().Where(c => sourceIds.Contains(c.SourceId) && c.Enabled).ToListAsync(ct)
            : mappedChans;

        // Keep the guide fresh for those sources (rate-limited; the next sweep searches the refreshed data).
        foreach (var sid in sourceIds) await MaybeRefreshEpgAsync(scope, db, sid, t.Title, ct);

        // Effective EPG id → channel (prefer the provider tvg-id, else the name-matched id).
        // Keyed on the UPPERCASED tvg-id: Programme.EpgChannelId is COLLATE NOCASE, so the SQL filter below matches
        // case-insensitively while this dictionary did not — a provider whose lineup says "FoxSports3.au" and whose
        // XMLTV says "foxsports3.au" made every candidate on that channel invisible to the sweep, and the ticket
        // expired reporting "no re-air appeared". (The national fallback already normalises both sides this way.)
        var byKey = new Dictionary<(int Source, string Epg), Channel>();
        foreach (var c in candidates)
        {
            var eid = !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId;
            if (!string.IsNullOrEmpty(eid)) byKey.TryAdd((c.SourceId, eid!.ToUpperInvariant()), c);
        }
        if (byKey.Count == 0) { await BumpAsync(db, t.Id, now + interval, now, "mapped channels aren't linked to the guide", ct); return; }

        // A re-air must start after the game ended AND after now (only the future is recordable), and last at least
        // ~70% of the game's length. Search the guide window up to the ticket's expiry.
        var expectedLen = Math.Max(1800, t.EventEndUtc - t.EventStartUtc);
        var minLen = (long)(expectedLen * MinReplayDurationFrac);
        var earliest = Math.Max(t.EventEndUtc, now + 120); // 2-min lead so the pre-roll can still arm
        var effIds = byKey.Keys.Select(k => k.Epg).Distinct().ToList();

        // Both sides of the fixture, for the "shows THIS game" gate below. A two-team event demands BOTH teams in
        // the programme title (audit RESCUE-01) — a team-magazine show, a highlights block, or the same team's
        // OTHER game must never be "rescued" in place of the real matchup. Single-name events (motorsport…) have
        // no both-team notion, so they demand a much stronger overall title match instead.
        var query = string.IsNullOrWhiteSpace(t.MatchQuery) ? t.Title : t.MatchQuery;
        var (sideA, sideB) = ResolverService.EventSides(query);
        var twoSided = !ReferenceEquals(sideA, sideB);

        // ---- Catch-up first (v1.45): before waiting days for a re-air, try pulling the finished game straight
        // from the provider's tv_archive. Immediate, and it can't collide with live games (Opportunistic + the
        // live-preempt rule). Falls through to the re-air hunt when no archive-enabled channel can serve it.
        if (await TryCatchupAsync(scope, db, t, byKey, query, sideA, sideB, twoSided, now, ct)) return;

        // Page through the WHOLE window by keyset instead of scoring an arbitrary 400-row prefix — with
        // whole-source search enabled, early unrelated programmes used to crowd the real re-air out of the
        // candidate set entirely (audit RESCUE-04). Hard cap is a runaway backstop, far above any real window.
        var found = new List<(Programme P, double Score, Channel Chan, int Coverage)>();
        long curStart = long.MinValue; var curId = 0; var scanned = 0;
        while (scanned < 50_000)
        {
            var cs = curStart; var ci = curId;
            var page = await db.Programmes.AsNoTracking()
                .Where(p => sourceIds.Contains(p.SourceId) && effIds.Contains(p.EpgChannelId)
                            && p.StartUtc >= earliest && p.StartUtc <= t.ExpiresUtc && (p.StopUtc - p.StartUtc) >= minLen
                            && (p.StartUtc > cs || (p.StartUtc == cs && p.Id > ci)))
                .OrderBy(p => p.StartUtc).ThenBy(p => p.Id).Take(500).ToListAsync(ct);
            if (page.Count == 0) break;
            scanned += page.Count;
            curStart = page[^1].StartUtc; curId = page[^1].Id;
            foreach (var p in page)
            {
                var chan = byKey.GetValueOrDefault((p.SourceId, p.EpgChannelId.ToUpperInvariant()));
                if (chan is null) continue;
                var score = ResolverService.EventSimilarity(p.Title, query, sideA, sideB);
                if (twoSided)
                {
                    if (score < MinTitleScore || !ResolverService.ShowsBothTeams(p.Title, sideA, sideB)) continue;
                }
                else if (score < MinSingleSidedScore) continue;
                found.Add((p, score, chan, ResolverService.SideTokenCoverage(p.Title, sideA, sideB)));
            }
        }

        if (found.Count == 0) { await BumpAsync(db, t.Id, now + interval, now, "no re-air in the guide yet", ct); return; }

        // The title NAMING MORE of the fixture wins first, then the best similarity, then the earliest air. Ranking
        // on similarity alone let a terser wrong programme (a same-city fixture from another competition) beat the
        // fuller right one, because Jaccard punishes the extra words in a complete title. Two DIFFERENT programmes
        // at the same coverage and within the score margin is still a coin flip (doubleheaders) — wait for a
        // clearer guide rather than guess. The same title on another channel/time is just the same re-air.
        var ordered = found.OrderByDescending(x => x.Coverage).ThenByDescending(x => x.Score).ThenBy(x => x.P.StartUtc).ToList();
        var best = ordered[0];
        var rival = ordered.Skip(1).FirstOrDefault(x => !string.Equals(x.P.Title, best.P.Title, StringComparison.OrdinalIgnoreCase));
        if (rival.P is not null && rival.Coverage == best.Coverage && best.Score - rival.Score < AmbiguityMargin)
        {
            await BumpAsync(db, t.Id, now + interval, now,
                "two different programmes match almost equally — waiting for a clearer guide", ct);
            return;
        }

        // Schedule the replay: Opportunistic priority (never preempts a live game), EventId-linked so finalize files
        // it exactly like the original would have, RescueTicketId-linked so the sweep can follow its outcome.
        var ch = best.Chan;
        await _gate.WriteAsync(async () =>
        {
            var fresh = await db.RescueTickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
            if (fresh is null || fresh.State != RescueTicketState.Open) return;
            var rep = new RecordingEntity
            {
                EventId = t.EventId, ChannelId = ch.Id, SourceId = ch.SourceId, StreamId = ch.StreamId,
                StartUtc = best.P.StartUtc, EndUtc = best.P.StopUtc, PrePadS = 60, PostPadS = 120,
                Title = t.Title, MatchQuery = t.MatchQuery, Priority = RecordingPriority.Opportunistic,
                // Locked to the exact re-air the sweep chose (audit RESCUE-02): the EPG re-pick scores the ORIGINAL
                // event's window and would happily drag this replay back to a channel that carried the original
                // broadcast but isn't carrying the re-air.
                ChannelLocked = true,
                RescueTicketId = t.Id, State = RecordingState.Pending, CreatedUtc = now, UpdatedUtc = now,
            };
            db.Recordings.Add(rep);
            await db.SaveChangesAsync(ct);
            fresh.State = RescueTicketState.Scheduled;
            fresh.ReplayRecordingId = rep.Id;
            fresh.LastSweepUtc = now;
            fresh.Note = $"re-air found on {ch.Name}";
            db.Notifications.Add(new Notification
            {
                RecordingId = t.RecordingId, TsUtc = now, Kind = NotificationKind.ReplayScheduled, Severity = Severity.Info,
                Message = $"found a re-air of “{t.Title}” on {ch.Name} at {EpochTime.ToDisplay(best.P.StartUtc):ddd d MMM HH:mm} — scheduled a replay",
            });
            await db.SaveChangesAsync(ct);
        }, ct);
        _log.LogInformation("[Rescue] ticket {Id}: scheduled replay of '{Title}' on {Chan} at {When} (score {Score:0.00})",
            ticketId, t.Title, ch.Name, best.P.StartUtc, best.Score);
    }

    private const int MaxCatchupAttempts = 2;      // archive pulls per ticket before falling back to re-airs only
    private const int CatchupIndexLagS = 600;      // providers index the archive shortly after air — don't pull early
    private const int CatchupMaxPullS = 8 * 3600;  // runaway cap on a single pull
    private const int CatchupHeadPadS = 120;       // archive head-room before the listed/event start
    private const int CatchupTailPadS = 600;       // …and after the listed/event end (post-game handshakes)

    /// <summary>Try to serve this ticket from the provider's catch-up archive: find a channel whose tv_archive
    /// still covers the game — preferring one whose guide HISTORY (7-day retention) corroborates that the fixture
    /// actually aired there, falling back to the original recording's channel when the ticket allows
    /// uncorroborated pulls — and schedule an immediate fast archive download. Returns true when a pull was
    /// scheduled (the sweep stops for this ticket; settle re-opens it if the pull fails).</summary>
    private async Task<bool> TryCatchupAsync(IServiceScope scope, DVarrDbContext db, RescueTicket t,
        Dictionary<(int Source, string Epg), Channel> byKey, string query,
        HashSet<string> sideA, HashSet<string> sideB, bool twoSided, long now, CancellationToken ct)
    {
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        if (!await settings.GetBoolAsync("catchup_enabled")) return false;
        if (t.CatchupAttempts >= MaxCatchupAttempts) return false;
        if (now < t.EventEndUtc + CatchupIndexLagS) return false;

        var winStart = t.EventStartUtc - 1800;
        var winEnd = t.EventEndUtc + 1800;

        // The archive must still hold the game's start (with an hour of safety so a pull can't fall off the
        // window's trailing edge mid-download).
        bool ArchiveCovers(Channel c) =>
            c.TvArchive && (c.TvArchiveDuration ?? 0) > 0
            && t.EventStartUtc >= now - (long)c.TvArchiveDuration!.Value * 86400 + 3600;

        (Channel Chan, long PullStart, long PullEnd, double Sim, bool Corroborated, int Coverage)? best = null;

        foreach (var kv in byKey)
        {
            var chan = kv.Value;
            if (!ArchiveCovers(chan)) continue;
            var progs = await db.Programmes.AsNoTracking()
                .Where(p => p.SourceId == chan.SourceId && p.EpgChannelId == kv.Key.Epg && p.StopUtc > winStart && p.StartUtc < winEnd)
                .OrderBy(p => p.StartUtc).Select(p => new { p.StartUtc, p.StopUtc, p.Title }).Take(50).ToListAsync(ct);
            foreach (var p in progs)
            {
                var score = ResolverService.EventSimilarity(p.Title, query, sideA, sideB);
                if (twoSided)
                {
                    if (score < MinTitleScore || !ResolverService.ShowsBothTeams(p.Title, sideA, sideB)) continue;
                }
                else if (score < MinSingleSidedScore) continue;
                // Same ranking rule as the re-air hunt: the listing that NAMES more of the fixture wins before the
                // one that merely scores higher (see SideTokenCoverage) — a catch-up pull commits to a download.
                var coverage = ResolverService.SideTokenCoverage(p.Title, sideA, sideB);
                if (best is null || coverage > best.Value.Coverage
                    || (coverage == best.Value.Coverage && score > best.Value.Sim))
                    best = (chan, p.StartUtc - CatchupHeadPadS, Math.Max(p.StopUtc, t.EventEndUtc) + CatchupTailPadS, score, true, coverage);
            }
        }

        // No guide-history corroboration anywhere — pull the ORIGINAL recording's channel over the event window,
        // unless this ticket demands a verified airing (an uncorroborated completion re-pulling the same channel
        // would just download the same wrong programme again).
        if (best is null && !t.RequireCorroborated)
        {
            var origChannelId = await db.Recordings.AsNoTracking().Where(r => r.Id == t.RecordingId)
                .Select(r => (int?)r.ChannelId).FirstOrDefaultAsync(ct);
            if (origChannelId is { } ocid)
            {
                var oc = byKey.Values.FirstOrDefault(c => c.Id == ocid)
                         ?? await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ocid && c.Enabled
                                && db.Sources.Any(s => s.Id == c.SourceId && s.Enabled), ct);
                if (oc is not null && oc.Enabled && ArchiveCovers(oc))
                    best = (oc, t.EventStartUtc - CatchupHeadPadS, t.EventEndUtc + CatchupTailPadS, 0, false, 0);
            }
        }
        if (best is null) return false;

        var pick = best.Value;
        // Same +3600 safety margin ArchiveCovers uses: a pull anchored exactly at the retention edge would lose
        // its head as the window rolls forward mid-download.
        var pullStart = Math.Max(pick.PullStart, now - (long)(pick.Chan.TvArchiveDuration ?? 1) * 86400 + 3600);
        var pullEnd = Math.Min(pick.PullEnd, now - 60); // never ask the archive for the future
        if (pullEnd - pullStart < 600) return false;    // nothing meaningful left to pull
        // If the window exceeds the runaway cap, trim the HEAD, not the tail — house rule: never cut the ending
        // off a game (a bad guide StopUtc must not cost the final minutes).
        if (pullEnd - pullStart > CatchupMaxPullS) pullStart = pullEnd - CatchupMaxPullS;
        var pullDur = (int)(pullEnd - pullStart);

        var scheduled = false;
        await _gate.WriteAsync(async () =>
        {
            var fresh = await db.RescueTickets.FirstOrDefaultAsync(x => x.Id == t.Id, ct);
            if (fresh is null || fresh.State != RescueTicketState.Open) return;
            var rep = new RecordingEntity
            {
                EventId = t.EventId, ChannelId = pick.Chan.Id, SourceId = pick.Chan.SourceId, StreamId = pick.Chan.StreamId,
                // EndUtc = the pull's wall-clock timeout: slack for ~1x-realtime archives / arm latency / shape
                // probing (a clean chunk-exhaustion still ends the pull early).
                StartUtc = now, EndUtc = now + pullDur + Math.Max(1800, pullDur / 2), PrePadS = 0, PostPadS = 0,
                Title = t.Title, MatchQuery = t.MatchQuery, Priority = RecordingPriority.Opportunistic,
                ChannelLocked = true, RescueTicketId = t.Id, State = RecordingState.Pending,
                CatchupSourceStartUtc = pullStart, CatchupDurationS = pullDur,
                CreatedUtc = now, UpdatedUtc = now,
            };
            db.Recordings.Add(rep);
            await db.SaveChangesAsync(ct);
            fresh.State = RescueTicketState.Scheduled;
            fresh.ReplayRecordingId = rep.Id;
            fresh.CatchupAttempts++;
            fresh.LastSweepUtc = now;
            fresh.Note = pick.Corroborated
                ? $"pulling from {pick.Chan.Name}'s catch-up archive (guide-verified airing)"
                : $"pulling from {pick.Chan.Name}'s catch-up archive (event window — no guide listing to verify against)";
            db.Notifications.Add(new Notification
            {
                RecordingId = t.RecordingId, TsUtc = now, Kind = NotificationKind.ReplayScheduled, Severity = Severity.Info,
                Message = $"pulling “{t.Title}” from {pick.Chan.Name}'s catch-up archive ({pullDur / 60} min) — downloading now",
            });
            await db.SaveChangesAsync(ct);
            scheduled = true;
        }, ct);
        if (scheduled)
            _log.LogInformation("[Rescue] ticket {Id}: scheduled catch-up pull of '{Title}' from {Chan} (archive {Start}, {Min} min, {Ver})",
                t.Id, t.Title, pick.Chan.Name, pullStart, pullDur / 60, pick.Corroborated ? $"guide-verified {pick.Sim:0.00}" : "unverified window");
        return scheduled;
    }

    /// <summary>Opportunistically refresh a source's guide when it's stale (&gt;12h), rate-limited to once/30min per
    /// source — a re-air within days is only findable against a reasonably current guide.</summary>
    private async Task MaybeRefreshEpgAsync(IServiceScope scope, DVarrDbContext db, int sourceId, string title, CancellationToken ct)
    {
        var src = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (src is null || !src.Enabled) return;
        if (src.LastEpgSyncUtc is { } last && EpochTime.Now() - last < 12 * 3600) return;
        var now = EpochTime.Now();
        var prev = _lastRefresh.GetOrAdd(sourceId, 0);
        if (now - prev < RefreshCooldownS || !_lastRefresh.TryUpdate(sourceId, now, prev)) return;
        try
        {
            var epg = scope.ServiceProvider.GetRequiredService<EpgIngestService>();
            var r = await epg.SyncSourceEpgAsync(sourceId, ct);
            _log.LogInformation("[Rescue] guide refresh source {Id} for '{Title}': {Status}", sourceId, title, r.Ok ? $"ok ({r.Programmes})" : r.Error);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Rescue] guide refresh failed for source {Id}", sourceId); }
    }

    private Task BumpAsync(DVarrDbContext db, int ticketId, long nextSweep, long now, string note, CancellationToken ct)
        => UpdateTicketAsync(db, ticketId, x => { x.NextSweepUtc = nextSweep; x.LastSweepUtc = now; x.Note = note; }, ct);

    private async Task ExpireAsync(DVarrDbContext db, RescueTicket t, long now, CancellationToken ct)
    {
        await UpdateTicketAsync(db, t.Id, x => { x.State = RescueTicketState.GaveUp; x.Note = "no re-air appeared before the deadline"; }, ct);
        await NotifyAsync(db, t.RecordingId, NotificationKind.ReplayGaveUp, Severity.Warn,
            $"gave up hunting for a re-air of “{t.Title}” — none appeared in the guide in time", now, ct);
        _log.LogInformation("[Rescue] ticket {Id} '{Title}' expired (gave up)", t.Id, t.Title);
    }

    private Task UpdateTicketAsync(DVarrDbContext db, int ticketId, Action<RescueTicket> mutate, CancellationToken ct)
        => _gate.WriteAsync(async () =>
        {
            var t = await db.RescueTickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
            if (t is null) return;
            mutate(t);
            await db.SaveChangesAsync(ct);
        }, ct);

    private Task NotifyAsync(DVarrDbContext db, int? recordingId, NotificationKind kind, Severity sev, string msg, long now, CancellationToken ct)
        => _gate.WriteAsync(async () =>
        {
            db.Notifications.Add(new Notification { RecordingId = recordingId, TsUtc = now, Kind = kind, Severity = sev, Message = msg });
            await db.SaveChangesAsync(ct);
        }, ct);
}
