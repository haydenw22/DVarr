using System.Collections.Concurrent;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Ingest;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

/// <summary>
/// Guide re-pick (docs: "map Fox 503+504 and record whichever the guide says has the game"). The resolver
/// already scores mapped channels by guide-title similarity, but it runs at PLACEMENT — days before the provider's
/// guide can see the event, so the pick degenerates to rank order. This service re-runs that same resolver for any
/// recording starting within the sweep horizon (48h — as soon as the guide lists the game, the Scheduled list shows
/// the corrected channel; issue #9) and re-points the recording (SAME credential only — slot planning is untouched)
/// when another mapped channel's guide actually shows the event.
/// It also keeps the guide fresh: if this source's last successful EPG sync is >12h old (or never), it kicks a
/// background refresh before re-picking (the next sweep re-picks against the fresh data); and if the guide is blank for
/// every mapped channel close to start it does the same. Both are rate-limited by a shared 30-min per-source cooldown,
/// and the ingest's per-source semaphore + last-known-good swap make an opportunistic refresh safe. Manual choices are
/// respected via Recording.ChannelLocked.
/// </summary>
public sealed class EpgRepickService
{
    // Tuning (constants, not settings — one kill-switch setting `epg_repick_enabled` governs the feature):
    private const double MinEpgScore = 0.25;   // proposed channel must actually look like the event
    private const double Hysteresis = 0.10;    // and beat the current channel by this much (no tick-to-tick flapping)
    private const int BlankRefreshWindowS = 3600;      // only chase a blank guide when the event is this close (a blank guide days out is normal)
    private const int StaleEpgS = 12 * 3600;           // refresh the source's guide if its last good sync is older than this
    private const int RefreshCooldownS = 30 * 60;      // at most one opportunistic EPG refresh per source per 30 min
    private const double PinOverrideScore = 0.5;       // a PINNED channel is only overridden by a STRONG both-team match elsewhere

    // Per-source last opportunistic-refresh stamp (static: the service is scoped per tick/request).
    private static readonly ConcurrentDictionary<int, long> _lastRefresh = new();

    private readonly DVarrDbContext _db;
    private readonly ResolverService _resolver;
    private readonly EpgIngestService _epg;
    private readonly DbWriteGate _gate;
    private readonly SettingsService _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EpgRepickService> _log;

    public EpgRepickService(DVarrDbContext db, ResolverService resolver, EpgIngestService epg, DbWriteGate gate,
        SettingsService settings, IServiceScopeFactory scopes, ILogger<EpgRepickService> log)
    { _db = db; _resolver = resolver; _epg = epg; _gate = gate; _settings = settings; _scopes = scopes; _log = log; }

    /// <summary>Re-pick the recording's channel from the live guide. Returns true if the recording was re-pointed.
    /// Safe no-op for manual recordings (no event), non-Pending states, locked channels, or when disabled.</summary>
    public async Task<bool> TryRepickAsync(int recordingId, CancellationToken ct = default)
    {
        var rec = await _db.Recordings.FindAsync(new object?[] { recordingId }, ct);
        if (rec is null || rec.State != RecordingState.Pending || rec.EventId is not { } eventId || rec.ChannelLocked) return false;
        if (!await _settings.GetBoolAsync("epg_repick_enabled")) return false;
        var ev = await _db.Events.FindAsync(new object?[] { eventId }, ct);
        if (ev is null) return false;

        var now = EpochTime.Now();

        // Stale-guide refresh: the provider's EPG often has no content 24h out, so re-picking is worthless against an
        // old guide. If this source's last successful EPG sync is >12h old (or it never synced), kick a background
        // refresh (shared 30-min per-source cooldown guards against repeated kicks) and re-pick against the current data
        // anyway — the next sweep after the refresh lands re-picks against the fresh guide.
        var src = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rec.SourceId, ct);
        if (src is not null && (src.LastEpgSyncUtc is not { } last || now - last > StaleEpgS))
            KickEpgRefresh(rec.SourceId, ev.Title);

        var res = await _resolver.ResolveAsync(eventId, ct, restrictSourceId: rec.SourceId);

        // Blank-guide chase: if NO mapped channel on this credential has any programme overlapping the event window
        // and the event is close, kick a background refresh of that source's guide (rate-limited; the next sweep then
        // re-picks against fresh data). Detected explicitly — a zero EPG score alone could just mean bad titles.
        if (ev.StartUtc > now && ev.StartUtc - now <= BlankRefreshWindowS && await GuideBlankAsync(rec.SourceId, ev, ct))
            KickEpgRefresh(rec.SourceId, ev.Title);

        if (!res.Ok || res.Primary is null) return false;
        var all = new List<ResolvedChannel> { res.Primary };
        all.AddRange(res.Fallbacks);
        // The guide's pick: among the mapped candidates, the channel whose EPG best matches the event (total score
        // breaks ties). Judging by EpgScore — not total score — is what lets a nationally-broadcast game move OFF a
        // pinned or team-scoped channel whose own guide doesn't show it (issue #5): the pin/team scope governs
        // placement, the guide governs where the game actually airs. The MinEpgScore + Hysteresis gates below mean a
        // channel whose guide DOES show the event is never abandoned on a weak or merely-similar match elsewhere.
        var best = all.OrderByDescending(c => c.EpgScore).ThenByDescending(c => c.Score).First();
        var cur = all.FirstOrDefault(c => c.ChannelId == rec.ChannelId);
        var curEpg = cur?.EpgScore ?? 0;

        // National-broadcast fallback: when NO mapped channel actually shows this game (best mapped EPG match is
        // weak/zero — EpgScore now only counts a both-team match), the game may be on a channel the user didn't map
        // (e.g. ESPN). Search the whole credential for a both-team match and re-point there, instead of recording a
        // mapped channel that isn't carrying the game. The mapped ladder rides along so the move keeps same-
        // credential fallbacks, and a pinned current channel demands a stronger match to move off.
        if (best.EpgScore < MinEpgScore && await TryNationalFallbackAsync(rec, ev, all, cur is { Pinned: true }, ct)) return true;

        if (best.ChannelId == rec.ChannelId) return false;                       // already on the guide's pick
        if (best.EpgScore < MinEpgScore || best.EpgScore < curEpg + Hysteresis) return false; // move only for a real guide reason
        // Protect a PINNED current channel: only a strong, unambiguous both-team match elsewhere overrides the user's pin.
        if (cur is { Pinned: true } && best.EpgScore < PinOverrideScore) return false;

        var fromName = (await _db.Channels.FindAsync(new object?[] { rec.ChannelId }, ct))?.Name ?? $"#{rec.ChannelId}";
        var moved = false;
        await _gate.WriteAsync(async () =>
        {
            // Re-check inside the gate: the recording may have armed/cancelled since the un-gated read.
            var fresh = await _db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordingId);
            if (fresh is null || fresh.State != RecordingState.Pending || fresh.ChannelLocked || fresh.ChannelId != rec.ChannelId) return;
            // Same-credential re-point (RecordingRepoint deletes the old fallback ladder; rebuild it from this resolve).
            await RecordingRepoint.ApplyAsync(_db, recordingId, best.SourceId, best.ChannelId, best.StreamId, now);
            var rank = 2;
            foreach (var fb in res.Fallbacks.Where(f => f.ChannelId != best.ChannelId).DistinctBy(f => f.ChannelId))
                _db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = recordingId, Rank = rank++, ChannelId = fb.ChannelId, SourceId = best.SourceId });
            _db.Notifications.Add(new Notification
            {
                RecordingId = recordingId, TsUtc = now, Kind = NotificationKind.EpgRepick, Severity = Severity.Info,
                Message = $"guide match: '{ev.Title}' is on '{best.ChannelName}' (EPG {best.EpgScore:0.00} vs {curEpg:0.00} on '{fromName}') — recording moved",
            });
            await _db.SaveChangesAsync(ct);
            moved = true;
        }, ct);
        if (!moved) return false;

        // RecordingRepoint bypasses the change tracker, so the tracked `rec` is stale — detach it so a caller in the
        // SAME scope (e.g. RecorderService.TryStartAsync re-loading the row) reads the re-pointed values from the DB.
        _db.Entry(rec).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        _log.LogInformation("[EpgRepick] '{Title}' moved {From} → {To} (EPG {New:0.00} vs {Cur:0.00})", ev.Title, fromName, best.ChannelName, best.EpgScore, curEpg);
        return true;
    }

    /// <summary>The effective EPG ids (provider tvg-id, else the name-matched id) of the channels mapped to the
    /// event's league on this credential.</summary>
    private async Task<List<string>> MappedEffectiveEpgIdsAsync(int sourceId, int leagueId, CancellationToken ct)
    {
        var chIds = await _db.LeagueChannelMaps.Where(m => m.LeagueId == leagueId).Select(m => m.ChannelId).ToListAsync(ct);
        if (chIds.Count == 0) return new();
        return (await _db.Channels.Where(c => chIds.Contains(c.Id) && c.SourceId == sourceId).Select(c => new { c.EpgChannelId, c.MatchedEpgId }).ToListAsync(ct))
            .Select(c => !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId)
            .Where(e => !string.IsNullOrEmpty(e)).Select(e => e!).Distinct().ToList();
    }

    /// <summary>True when no channel mapped to the event's league (on this credential) has ANY programme overlapping
    /// the event window — i.e. the guide can't see the event yet (stale/blank), as opposed to merely not matching.</summary>
    private async Task<bool> GuideBlankAsync(int sourceId, Event ev, CancellationToken ct)
    {
        var hasMaps = await _db.LeagueChannelMaps.AnyAsync(m => m.LeagueId == ev.LeagueId, ct);
        if (!hasMaps) return false;
        var effIds = await MappedEffectiveEpgIdsAsync(sourceId, ev.LeagueId, ct);
        if (effIds.Count == 0) return true; // no channel even linked to the guide → a refresh (re-runs name-matching) may fix it
        var winStart = ev.StartUtc - 1800;
        var winEnd = ev.EndUtc ?? ev.StartUtc + 7200;
        return !await _db.Programmes.AnyAsync(p => p.SourceId == sourceId && effIds.Contains(p.EpgChannelId) && p.StopUtc > winStart && p.StartUtc < winEnd, ct);
    }

    // "TBD vs TBD", "TBA", "To Be Announced/Determined/Confirmed"… — a network holding a slot it hasn't named yet.
    private static readonly System.Text.RegularExpressions.Regex PlaceholderRx =
        new(@"\b(tbd|tba|to be (announced|determined|confirmed|decided))\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when a channel mapped to the event's league has a PLACEHOLDER programme over the event window.
    /// That means the slot is reserved but the guide hasn't named the fixture yet — NOT that the game moved to an
    /// unmapped channel. (Real case: a mapped 4K sports channel listing "TBD vs TBD" for the slot lost the game to
    /// an unmapped channel whose guide had firmed up earlier; the mapped guide named the fixture hours later.)</summary>
    private async Task<bool> MappedGuideHasPlaceholderAsync(int sourceId, Event ev, CancellationToken ct)
    {
        var effIds = await MappedEffectiveEpgIdsAsync(sourceId, ev.LeagueId, ct);
        if (effIds.Count == 0) return false;
        var winStart = ev.StartUtc - 1800;
        var winEnd = ev.EndUtc ?? ev.StartUtc + 7200;
        var titles = await _db.Programmes.AsNoTracking()
            .Where(p => p.SourceId == sourceId && effIds.Contains(p.EpgChannelId) && p.StopUtc > winStart && p.StartUtc < winEnd)
            .Select(p => p.Title).Take(200).ToListAsync(ct);
        return titles.Any(t => t is not null && PlaceholderRx.IsMatch(t));
    }

    /// <summary>National-broadcast fallback: when no MAPPED channel shows the event, search every channel on the
    /// recording's own credential for a programme that clearly shows THIS game (both teams, each proven by its own
    /// unique tokens) and re-point there — the game moved to a channel the user didn't map (e.g. ESPN). Same
    /// credential only (slot planning untouched). The move is deliberately NOT locked: if a mapped channel's guide
    /// later firms up with a real both-team match, the ordinary re-pick reclaims the game for the user's own lineup
    /// (their mapped channel is the one they chose — often the better feed). A mapped-channel PLACEHOLDER over the
    /// window ("TBD vs TBD") holds position entirely. Opt-out via national_fallback_enabled. Needs a two-team event
    /// title (skips single-name events like motorsport, where "both teams" is meaningless).</summary>
    private async Task<bool> TryNationalFallbackAsync(Data.Entities.Recording rec, Event ev,
        List<ResolvedChannel> mappedLadder, bool curPinned, CancellationToken ct)
    {
        if (!await _settings.GetBoolAsync("national_fallback_enabled")) return false;
        var (sideA, sideB) = ResolverService.EventSides(ev.Title);
        if (ReferenceEquals(sideA, sideB) || sideA.Count == 0 || sideB.Count == 0) return false; // need two distinct teams

        // A mapped channel holding the slot with a placeholder means the guide hasn't named the fixture yet — that
        // is not evidence the game moved. Stay put; the next sweep re-judges once the placeholder resolves.
        if (await MappedGuideHasPlaceholderAsync(rec.SourceId, ev, ct)) return false;

        var now = EpochTime.Now();
        // Every enabled channel on this credential that ISN'T already mapped to the league (those were just judged),
        // keyed by effective EPG id (provider tvg-id, else the name-matched id).
        var mapped = await _db.LeagueChannelMaps.Where(m => m.LeagueId == ev.LeagueId).Select(m => m.ChannelId).ToListAsync(ct);
        var chans = await _db.Channels.AsNoTracking()
            .Where(c => c.SourceId == rec.SourceId && c.Enabled && !mapped.Contains(c.Id))
            .Select(c => new { c.Id, c.StreamId, c.Name, c.EpgChannelId, c.MatchedEpgId })
            .ToListAsync(ct);
        var byEpg = new Dictionary<string, (int Id, int StreamId, string Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chans)
        {
            var eid = !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId;
            if (!string.IsNullOrEmpty(eid)) byEpg.TryAdd(eid!, (c.Id, c.StreamId, c.Name));
        }
        if (byEpg.Count == 0) return false;

        var winStart = ev.StartUtc - 1800;
        var winEnd = (ev.EndUtc ?? ev.StartUtc + 7200) + 1800;
        var effIds = byEpg.Keys.ToList();
        // Deterministic candidate set (audit EPG-03): without an ordering, SQLite may hand back a different 4096-row
        // prefix on every sweep and nondeterministically omit the true match on very large lineups.
        var progs = await _db.Programmes.AsNoTracking()
            .Where(p => p.SourceId == rec.SourceId && effIds.Contains(p.EpgChannelId) && p.StopUtc > winStart && p.StartUtc < winEnd)
            .OrderBy(p => p.StartUtc).ThenBy(p => p.Id)
            .Select(p => new { p.EpgChannelId, p.Title })
            .Take(4096).ToListAsync(ct);

        var best = progs
            .Where(p => ResolverService.ShowsBothTeams(p.Title, sideA, sideB))
            .Select(p => new { p, sim = ResolverService.Similarity(p.Title, ev.Title), ch = byEpg.GetValueOrDefault(p.EpgChannelId) })
            .Where(x => x.ch.Id != 0)
            .OrderByDescending(x => x.sim).FirstOrDefault();
        if (best is null) return false;
        // A PINNED mapped channel is the user's explicit "record it here" — only a strong, unambiguous match on an
        // unmapped channel justifies moving off it (same bar the mapped re-pick applies).
        if (curPinned && best.sim < PinOverrideScore) return false;

        var moved = false;
        await _gate.WriteAsync(async () =>
        {
            var fresh = await _db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rec.Id, ct);
            if (fresh is null || fresh.State != RecordingState.Pending || fresh.ChannelLocked || fresh.ChannelId == best.ch.Id) return;
            await RecordingRepoint.ApplyAsync(_db, rec.Id, rec.SourceId, best.ch.Id, best.ch.StreamId, now);
            // Deliberately NOT ChannelLocked: if a mapped channel's guide later firms up with a real both-team
            // match, the ordinary re-pick must be able to reclaim the game for the user's own lineup. Flapping is
            // prevented by the both-team + MinEpgScore + hysteresis gates on the mapped side, and this fallback
            // no-ops while the recording is already on the national pick.
            // Rebuild the same-credential fallback ladder from the MAPPED channels (RecordingRepoint just deleted
            // it — audit EPG-02): if the national channel's feed dies, the recorder walks back to the user's own
            // lineup instead of having nowhere to go.
            var rank = 2;
            foreach (var fb in mappedLadder.Where(f => f.SourceId == rec.SourceId && f.ChannelId != best.ch.Id).DistinctBy(f => f.ChannelId))
                _db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = rec.Id, Rank = rank++, ChannelId = fb.ChannelId, SourceId = rec.SourceId });
            _db.Notifications.Add(new Notification
            {
                RecordingId = rec.Id, TsUtc = now, Kind = NotificationKind.EpgRepick, Severity = Severity.Info,
                Message = $"national broadcast: '{ev.Title}' is on '{best.ch.Name}' (not a mapped channel) — recording moved there",
            });
            await _db.SaveChangesAsync(ct);
            moved = true;
        }, ct);
        if (moved)
        {
            _db.Entry(rec).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            _log.LogInformation("[EpgRepick] national fallback: '{Title}' → {Chan} on credential {Src}", ev.Title, best.ch.Name, rec.SourceId);
        }
        return moved;
    }

    private void KickEpgRefresh(int sourceId, string eventTitle)
    {
        var now = EpochTime.Now();
        var last = _lastRefresh.GetOrAdd(sourceId, 0);
        if (now - last < RefreshCooldownS || !_lastRefresh.TryUpdate(sourceId, now, last)) return;
        _log.LogInformation("[EpgRepick] guide blank around '{Title}' — refreshing source {Id} EPG in the background", eventTitle, sourceId);
        _ = Task.Run(async () =>
        {
            try
            {
                // Own scope: this outlives the caller's request/tick scope. The ingest's per-source semaphore fast-fails
                // if a sync is already running, and last-known-good keeps the old guide on any failure.
                using var scope = _scopes.CreateScope();
                var epg = scope.ServiceProvider.GetRequiredService<EpgIngestService>();
                var r = await epg.SyncSourceEpgAsync(sourceId, CancellationToken.None);
                _log.LogInformation("[EpgRepick] opportunistic EPG refresh source {Id}: {Status}", sourceId, r.Ok ? $"ok ({r.Programmes} programmes)" : r.Error);
            }
            catch (Exception ex) { _log.LogWarning(ex, "[EpgRepick] opportunistic EPG refresh failed for source {Id}", sourceId); }
        });
    }
}
