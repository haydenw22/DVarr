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
    private readonly ILogger<RescueSweepService> _log;

    private const double MinTitleScore = 0.30;         // an EPG programme must actually look like the event
    private const double MinSingleSidedScore = 0.50;   // single-name events (motorsport…) have no both-team gate, so demand a much stronger title match
    private const double AmbiguityMargin = 0.05;       // two DIFFERENT programmes scoring this close is a coin flip — wait for a clearer guide
    private const double MinReplayDurationFrac = 0.70; // ...and be at least this fraction of the game's length (not a highlights show)
    private const int RefreshCooldownS = 30 * 60;      // at most one opportunistic EPG refresh per source per 30 min
    private static readonly ConcurrentDictionary<int, long> _lastRefresh = new();

    public RescueSweepService(IServiceScopeFactory scopes, DbWriteGate gate, ILogger<RescueSweepService> log)
    { _scopes = scopes; _gate = gate; _log = log; }

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
                    if (now > t.ExpiresUtc) await ExpireAsync(db, t, now, ct);
                    else await UpdateTicketAsync(db, t.Id, x =>
                    {
                        x.State = RescueTicketState.Open; x.ReplayRecordingId = null; x.NextSweepUtc = now;
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

        var mappedChans = await db.Channels.AsNoTracking().Where(c => mapped.Contains(c.Id) && c.Enabled).ToListAsync(ct);
        var sourceIds = mappedChans.Select(c => c.SourceId).Distinct().ToList();
        var candidates = t.WholeSource
            ? await db.Channels.AsNoTracking().Where(c => sourceIds.Contains(c.SourceId) && c.Enabled).ToListAsync(ct)
            : mappedChans;

        // Keep the guide fresh for those sources (rate-limited; the next sweep searches the refreshed data).
        foreach (var sid in sourceIds) await MaybeRefreshEpgAsync(scope, db, sid, t.Title, ct);

        // Effective EPG id → channel (prefer the provider tvg-id, else the name-matched id).
        var byKey = new Dictionary<(int Source, string Epg), Channel>();
        foreach (var c in candidates)
        {
            var eid = !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId;
            if (!string.IsNullOrEmpty(eid)) byKey.TryAdd((c.SourceId, eid!), c);
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

        // Page through the WHOLE window by keyset instead of scoring an arbitrary 400-row prefix — with
        // whole-source search enabled, early unrelated programmes used to crowd the real re-air out of the
        // candidate set entirely (audit RESCUE-04). Hard cap is a runaway backstop, far above any real window.
        var found = new List<(Programme P, double Score, Channel Chan)>();
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
                var chan = byKey.GetValueOrDefault((p.SourceId, p.EpgChannelId));
                if (chan is null) continue;
                var score = ResolverService.Similarity(p.Title, query);
                if (twoSided)
                {
                    if (score < MinTitleScore || !ResolverService.ShowsBothTeams(p.Title, sideA, sideB)) continue;
                }
                else if (score < MinSingleSidedScore) continue;
                found.Add((p, score, chan));
            }
        }

        if (found.Count == 0) { await BumpAsync(db, t.Id, now + interval, now, "no re-air in the guide yet", ct); return; }

        // Best title match wins; earliest air breaks ties. But two DIFFERENT programmes scoring within the margin
        // is a coin flip (doubleheaders, similarly-named fixtures) — wait for a clearer guide rather than guess.
        // The same title on another channel/time is just the same re-air and never blocks.
        var ordered = found.OrderByDescending(x => x.Score).ThenBy(x => x.P.StartUtc).ToList();
        var best = ordered[0];
        var rival = ordered.Skip(1).FirstOrDefault(x => !string.Equals(x.P.Title, best.P.Title, StringComparison.OrdinalIgnoreCase));
        if (rival.P is not null && best.Score - rival.Score < AmbiguityMargin)
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
