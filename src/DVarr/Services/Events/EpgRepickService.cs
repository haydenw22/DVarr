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
/// the corrected channel; issue #9) and re-points the recording when another mapped channel's guide actually shows
/// the event (same credential — slot planning untouched). The NATIONAL fallback additionally searches unmapped
/// channels on EVERY enabled credential, moving cross-credential only when that credential's slot is free for the
/// whole padded window.
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
    private readonly TheSportsDbClient _tsdb;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EpgRepickService> _log;

    public EpgRepickService(DVarrDbContext db, ResolverService resolver, EpgIngestService epg, DbWriteGate gate,
        SettingsService settings, TheSportsDbClient tsdb, IServiceScopeFactory scopes, ILogger<EpgRepickService> log)
    { _db = db; _resolver = resolver; _epg = epg; _gate = gate; _settings = settings; _tsdb = tsdb; _scopes = scopes; _log = log; }

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
        // (e.g. ESPN, NBC). Search the whole lineup — every enabled credential — for a both-team match, then for a
        // channel NAMED for the broadcaster TheSportsDB lists, and re-point there instead of recording a mapped
        // channel that isn't carrying the game. A pinned current channel demands a stronger match to move off.
        if (best.EpgScore < MinEpgScore && await TryNationalFallbackAsync(rec, ev, all, cur is { Pinned: true }, ct)) return true;

        // Nothing on this credential corroborates the fixture — no mapped channel's guide shows both teams, and the
        // fallback above found nothing either. The capture will still go ahead on its mapped channel, which may be
        // showing a blackout slate or studio filler. Say so ONCE, close to air, instead of handing back three hours
        // of the wrong programme with no indication. A warning only: guide data is patchy and must never block a
        // recording that would otherwise have worked.
        if (best.EpgScore < MinEpgScore) await WarnNoGuideCorroborationAsync(rec, ev, ct);

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
            .OrderBy(p => p.StartUtc).Select(p => p.Title).Take(200).ToListAsync(ct);
        return titles.Any(t => t is not null && PlaceholderRx.IsMatch(t));
    }

    private const int CorroborationWarnWindowS = 3600;  // only warn once the game is close to air

    /// <summary>Tell the user, ONCE and close to air, that nothing in their lineup's guide shows this fixture — so the
    /// capture that is about to run may record a blackout slate or studio filler instead of the game. Deliberately a
    /// warning and not a block: provider guides are patchy, and a missing listing must never cancel a recording that
    /// would have worked.</summary>
    private async Task WarnNoGuideCorroborationAsync(Data.Entities.Recording rec, Event ev, CancellationToken ct)
    {
        var now = EpochTime.Now();
        if (ev.StartUtc - now > CorroborationWarnWindowS) return;   // days out the guide may still firm up — don't cry wolf
        // Once per recording: the re-pick sweep re-runs every tick.
        if (await _db.Notifications.AnyAsync(n => n.RecordingId == rec.Id && n.Kind == NotificationKind.NoGuideMatch, ct)) return;
        // Name the networks when we know them (the fallback's TV lookup may just have populated ev.Broadcast) — "it's
        // on NBC and Peacock, which you don't carry" is actionable in a way the generic warning isn't.
        var nets = SplitNetworks(ev.Broadcast);
        var msg = nets.Count > 0
            ? $"'{ev.Title}' is listed on {NetList(nets)} — no channel in your lineup carries it; recording the mapped channel anyway"
            : $"no channel in your lineup lists '{ev.Title}' — recording anyway, but it may be a national or streaming-only broadcast you don't receive";
        await _gate.WriteAsync(async () =>
        {
            _db.Notifications.Add(new Notification
            {
                RecordingId = rec.Id, TsUtc = now, Kind = NotificationKind.NoGuideMatch, Severity = Severity.Warn,
                Message = msg,
            });
            await _db.SaveChangesAsync(ct);
        }, ct);
        _log.LogWarning("[EpgRepick] '{Title}': nothing in the lineup lists this fixture{Nets} — the capture may record the wrong programme",
            ev.Title, nets.Count > 0 ? $" (listed on {NetList(nets)})" : "");
    }

    // Last decline reason logged per recording. The re-pick sweep re-runs on every schedule tick, so log at
    // Information (the level the in-app viewer keeps) only when the reason CHANGES — otherwise at most once every
    // 30 minutes per recording.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Reason, long At)> _lastDecline = new();

    private void LogDecline(int recordingId, string title, string reason)
    {
        var now = EpochTime.Now();
        var prev = _lastDecline.TryGetValue(recordingId, out var pv) ? pv : default;
        if (prev.Reason == reason && now - prev.At < 1800) return;
        _lastDecline[recordingId] = (reason, now);
        _log.LogInformation("[EpgRepick] no national-broadcast alternative for '{Title}': {Reason}", title, reason);
    }

    // Mirrors AutoScheduleService.SlotHolding: the states that occupy a credential's single stream slot. Used to
    // test whether ANOTHER credential can take this recording before a cross-credential national move.
    private static readonly RecordingState[] SlotHolding =
    {
        RecordingState.Pending, RecordingState.Starting, RecordingState.Recording, RecordingState.Recovering,
        RecordingState.FailingOver, RecordingState.Degraded, RecordingState.Stopping, RecordingState.Finalizing,
    };

    /// <summary>National-broadcast fallback: when no MAPPED channel shows the event, search the unmapped channels of
    /// EVERY enabled credential for the game, in two passes — (1) a guide programme that clearly shows THIS game
    /// (both teams, each proven by its own unique tokens); (2) when no guide anywhere names the teams (branded
    /// national listings — "Sunday Night Baseball"), a channel NAMED for a broadcaster TheSportsDB lists for the
    /// event, corroborated by sport/league-ish guide content so "FOX" can never land on FOX News. A cross-credential
    /// move happens only when that credential's slot is free for the whole padded window. The move is deliberately
    /// NOT locked: if a mapped channel's guide later firms up with a real both-team match, the ordinary re-pick
    /// reclaims the game for the user's own lineup. A mapped-channel PLACEHOLDER over the window ("TBD vs TBD")
    /// holds position entirely. Opt-out via national_fallback_enabled. Needs a two-team event title (skips
    /// single-name events like motorsport, where "both teams" is meaningless).</summary>
    private async Task<bool> TryNationalFallbackAsync(Data.Entities.Recording rec, Event ev,
        List<ResolvedChannel> mappedLadder, bool curPinned, CancellationToken ct)
    {
        // Every exit below says WHY. Reaching here already means NO mapped channel's guide showed this fixture, so the
        // recording is about to capture a channel that isn't carrying the game — the user needs to be able to find out
        // which step gave up (previously all these exits were silent).
        if (!await _settings.GetBoolAsync("national_fallback_enabled"))
        { LogDecline(rec.Id, ev.Title, "the national-broadcast fallback is turned off in Settings"); return false; }
        var (sideA, sideB) = ResolverService.EventSides(ev.Title);
        if (ReferenceEquals(sideA, sideB) || sideA.Count == 0 || sideB.Count == 0)
        { LogDecline(rec.Id, ev.Title, "couldn't split the event title into two distinct teams"); return false; }

        // A mapped channel holding the slot with a placeholder means the guide hasn't named the fixture yet — that
        // is not evidence the game moved. Stay put; the next sweep re-judges once the placeholder resolves.
        if (await MappedGuideHasPlaceholderAsync(rec.SourceId, ev, ct))
        { LogDecline(rec.Id, ev.Title, "a mapped channel's guide still shows a placeholder — waiting for it to firm up"); return false; }

        var now = EpochTime.Now();
        // League mapped channel ids in RANK order (the order matters when a cross-credential move rebuilds its
        // fallback ladder from whichever of these live on the target credential).
        var mapped = await _db.LeagueChannelMaps.Where(m => m.LeagueId == ev.LeagueId)
            .OrderBy(m => m.Rank).Select(m => m.ChannelId).ToListAsync(ct);
        // Every enabled unmapped channel on every ENABLED credential — the game may be on a network carried only by
        // the user's other login. Keyed (SourceId, effective EPG id): EPG ids repeat across sources.
        var enabledSources = await _db.Sources.AsNoTracking().Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct);
        var chans = await _db.Channels.AsNoTracking()
            .Where(c => enabledSources.Contains(c.SourceId) && c.Enabled && !mapped.Contains(c.Id))
            .Select(c => new { c.Id, c.SourceId, c.StreamId, c.Name, c.EpgChannelId, c.MatchedEpgId })
            .ToListAsync(ct);
        var byEpg = new Dictionary<(int Src, string Eid), (int Id, int SourceId, int StreamId, string Name)>();
        foreach (var c in chans)
        {
            var eid = !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId;
            if (!string.IsNullOrEmpty(eid)) byEpg.TryAdd((c.SourceId, eid!.ToUpperInvariant()), (c.Id, c.SourceId, c.StreamId, c.Name));
        }
        if (byEpg.Count == 0)
        { LogDecline(rec.Id, ev.Title, $"none of the {chans.Count} unmapped channel(s) on any login carry a guide id to search"); return false; }

        var winStart = ev.StartUtc - 1800;
        var winEnd = (ev.EndUtc ?? ev.StartUtc + 7200) + 1800;
        var effIds = byEpg.Keys.Select(k => k.Eid).Distinct().ToList();
        // Deterministic candidate set (audit EPG-03): without an ordering, SQLite may hand back a different 4096-row
        // prefix on every sweep and nondeterministically omit the true match on very large lineups.
        var progs = await _db.Programmes.AsNoTracking()
            .Where(p => enabledSources.Contains(p.SourceId) && effIds.Contains(p.EpgChannelId.ToUpper()) && p.StopUtc > winStart && p.StartUtc < winEnd)
            .OrderBy(p => p.StartUtc).ThenBy(p => p.Id)
            .Select(p => new { p.SourceId, p.EpgChannelId, p.Title })
            .Take(4096).ToListAsync(ct);

        // ---- Pass 1: a guide programme somewhere names BOTH teams. Same-credential candidates first (no slot
        // question); a cross-credential candidate must have that login free for the whole padded window.
        var titleHits = progs
            .Where(p => ResolverService.ShowsBothTeams(p.Title, sideA, sideB))
            .Select(p => new { p, sim = ResolverService.Similarity(p.Title, ev.Title), ch = byEpg.GetValueOrDefault((p.SourceId, p.EpgChannelId.ToUpperInvariant())) })
            .Where(x => x.ch.Id != 0)
            .OrderByDescending(x => x.ch.SourceId == rec.SourceId).ThenByDescending(x => x.sim)
            .ToList();
        if (curPinned && titleHits.Count > 0)
        {
            // A PINNED mapped channel is the user's explicit "record it here" — only a strong, unambiguous match on
            // an unmapped channel justifies moving off it (same bar the mapped re-pick applies).
            var strongest = titleHits.MaxBy(x => x.sim)!;
            titleHits = titleHits.Where(x => x.sim >= PinOverrideScore).ToList();
            if (titleHits.Count == 0)
            {
                LogDecline(rec.Id, ev.Title, $"best match outside your mapped channels was '{strongest.ch.Name}' ({strongest.sim:0.00}), below the {PinOverrideScore:0.00} needed to move off a pinned channel");
                return false;
            }
        }
        foreach (var hit in titleHits)
        {
            if (hit.ch.SourceId != rec.SourceId && !await SlotFreeAsync(hit.ch.SourceId, rec, ct))
            { LogDecline(rec.Id, ev.Title, $"'{hit.ch.Name}' on your other login shows the game, but that login is busy for this window"); continue; }
            return await MoveToAsync(rec, ev, mapped, mappedLadder, (hit.ch.Id, hit.ch.SourceId, hit.ch.StreamId, hit.ch.Name), now,
                $"national broadcast: '{ev.Title}' is on '{hit.ch.Name}'{(hit.ch.SourceId == rec.SourceId ? "" : " (your other login)")} — recording moved there", ct);
        }
        if (titleHits.Count > 0) return false; // every both-team candidate sat on a busy login (each decline logged)

        // ---- Pass 2: no guide anywhere names both teams — the national listing is usually BRANDED ("Sunday Night
        // Baseball", "MLB on NBC"). Ask TheSportsDB which networks carry the event and look for a channel NAMED for
        // one, requiring that channel's own guide to show sport/league-ish content over the window (so "FOX" can
        // never land on FOX News). Network-name evidence is weaker than a both-team title, so it never overrides a
        // pinned channel.
        var networks = await NetworksForEventAsync(ev, ct);
        if (networks.Count == 0)
        {
            LogDecline(rec.Id, ev.Title, $"no channel's guide names both teams in this window ({progs.Count} programme(s) searched across {byEpg.Count} channel(s)), and TheSportsDB lists no broadcaster to match by name — the game may be on a network you don't carry, or streaming-only");
            return false;
        }
        if (curPinned)
        {
            LogDecline(rec.Id, ev.Title, $"listed on {NetList(networks)}, but your current channel is pinned — a network-name match isn't strong enough evidence to move off a pin");
            return false;
        }

        var league = await _db.Leagues.AsNoTracking().FirstOrDefaultAsync(l => l.Id == ev.LeagueId, ct);
        var corroborate = TokenSet($"{league?.Name} {league?.Sport} {ev.Title}");
        var netCands = new List<(int Id, int SourceId, int StreamId, string Name, string Network, int NetScore, string Prog, double Sim)>();
        foreach (var c in chans)
        {
            var (network, netScore) = networks.Select(n => (n, s: NetworkNameMatch(c.Name, n)))
                .Where(x => x.s > 0).OrderByDescending(x => x.s).FirstOrDefault();
            if (network is null) continue;
            var eid = !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId;
            if (string.IsNullOrEmpty(eid)) continue;
            foreach (var p in progs.Where(p => p.SourceId == c.SourceId && string.Equals(p.EpgChannelId, eid, StringComparison.OrdinalIgnoreCase)))
            {
                if (p.Title is null || PlaceholderRx.IsMatch(p.Title)) continue;
                if (!TokenSet(p.Title).Overlaps(corroborate)) continue;   // guide must look like this sport/league/fixture
                netCands.Add((c.Id, c.SourceId, c.StreamId, c.Name, network, netScore, p.Title, ResolverService.Similarity(p.Title, ev.Title)));
            }
        }
        if (netCands.Count == 0)
        {
            LogDecline(rec.Id, ev.Title, $"listed on {NetList(networks)}, but no channel named for {(networks.Count == 1 ? "that network" : "those networks")} shows matching sport content in this window — it may be a network you don't carry, or streaming-only");
            return false;
        }
        // One candidate per channel (its best-matching programme); same credential first, then full-name network
        // matches over brand-only ones, then the closest guide title, then a deterministic name tiebreak.
        foreach (var cand in netCands
            .GroupBy(x => (x.SourceId, x.Id)).Select(g => g.OrderByDescending(x => x.Sim).First())
            .OrderByDescending(x => x.SourceId == rec.SourceId).ThenByDescending(x => x.NetScore).ThenByDescending(x => x.Sim)
            .ThenBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (cand.SourceId != rec.SourceId && !await SlotFreeAsync(cand.SourceId, rec, ct))
            { LogDecline(rec.Id, ev.Title, $"'{cand.Name}' on your other login matches {cand.Network}, but that login is busy for this window"); continue; }
            return await MoveToAsync(rec, ev, mapped, mappedLadder, (cand.Id, cand.SourceId, cand.StreamId, cand.Name), now,
                $"national broadcast: '{ev.Title}' is listed on {cand.Network} — moved to '{cand.Name}'{(cand.SourceId == rec.SourceId ? "" : " (your other login)")}, whose guide shows '{cand.Prog}'", ct);
        }
        return false; // every network-named candidate sat on a busy login (each decline logged)
    }

    /// <summary>True when the given credential has no other recording holding its single stream slot anywhere in
    /// this recording's padded window — the precondition for a cross-credential national move. If that login is
    /// (or will be) busy, the move is refused rather than silently stealing the slot from whatever the scheduler
    /// placed there.</summary>
    private async Task<bool> SlotFreeAsync(int sourceId, Data.Entities.Recording rec, CancellationToken ct)
    {
        var winStart = rec.StartUtc - rec.PrePadS;
        var winEnd = rec.EndUtc + rec.PostPadS;
        return !await _db.Recordings.AsNoTracking().AnyAsync(r => r.SourceId == sourceId && r.Id != rec.Id
            && SlotHolding.Contains(r.State)
            && r.StartUtc - r.PrePadS < winEnd && r.EndUtc + r.PostPadS > winStart, ct);
    }

    /// <summary>Re-point the recording to the chosen national channel and rebuild its fallback ladder on the TARGET
    /// credential (fallbacks must share the primary's source). Same-credential moves reuse the resolved mapped
    /// ladder; a cross-credential move rebuilds it from whichever of the league's mapped channels exist on that
    /// credential, in map-rank order — if the national channel's feed dies, the recorder walks back to the user's
    /// own lineup instead of having nowhere to go. Deliberately does NOT set ChannelLocked: if a mapped channel's
    /// guide later firms up with a real both-team match, the ordinary re-pick reclaims the game. Flapping is
    /// prevented by the both-team + MinEpgScore + hysteresis gates on the mapped side, and the fallback no-ops
    /// while the recording is already on the national pick.</summary>
    private async Task<bool> MoveToAsync(Data.Entities.Recording rec, Event ev, List<int> mappedRankOrder,
        List<ResolvedChannel> mappedLadder, (int Id, int SourceId, int StreamId, string Name) target, long now,
        string message, CancellationToken ct)
    {
        var moved = false;
        await _gate.WriteAsync(async () =>
        {
            var fresh = await _db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rec.Id, ct);
            if (fresh is null || fresh.State != RecordingState.Pending || fresh.ChannelLocked || fresh.ChannelId == target.Id) return;
            await RecordingRepoint.ApplyAsync(_db, rec.Id, target.SourceId, target.Id, target.StreamId, now);
            var rank = 2;
            if (target.SourceId == rec.SourceId)
            {
                foreach (var fb in mappedLadder.Where(f => f.SourceId == rec.SourceId && f.ChannelId != target.Id).DistinctBy(f => f.ChannelId))
                    _db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = rec.Id, Rank = rank++, ChannelId = fb.ChannelId, SourceId = rec.SourceId });
            }
            else
            {
                var onTarget = await _db.Channels.AsNoTracking()
                    .Where(c => c.SourceId == target.SourceId && c.Enabled && mappedRankOrder.Contains(c.Id) && c.Id != target.Id)
                    .Select(c => c.Id).ToListAsync(ct);
                foreach (var chId in mappedRankOrder.Where(onTarget.Contains))
                    _db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = rec.Id, Rank = rank++, ChannelId = chId, SourceId = target.SourceId });
            }
            _db.Notifications.Add(new Notification
            {
                RecordingId = rec.Id, TsUtc = now, Kind = NotificationKind.EpgRepick, Severity = Severity.Info,
                Message = message,
            });
            await _db.SaveChangesAsync(ct);
            moved = true;
        }, ct);
        if (moved)
        {
            _db.Entry(rec).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            _log.LogInformation("[EpgRepick] national fallback: '{Title}' → {Chan} on credential {Src}", ev.Title, target.Name, target.SourceId);
        }
        return moved;
    }

    // Per-event stamp of the last TV-station lookup ATTEMPT, so a transiently failing lookup can't hammer the API
    // every sweep tick. A success is persisted on the event (Broadcast; "" = none listed) and never re-fetched.
    private static readonly ConcurrentDictionary<int, long> _lastTvLookup = new();
    private const int TvLookupRetryS = 600;

    /// <summary>The broadcast networks for this event: from the synced schedule when the payload carried
    /// strTVStation, else fetched ONCE from TheSportsDB's per-event TV lookup (v2 /lookup/event_tv — the schedule
    /// payload doesn't carry broadcasters; verified live) and persisted. Country qualifiers are stripped from the
    /// stored names ("ESPN 2 Netherlands" → "ESPN 2") so they can match the user's channel names. The fallback only
    /// runs for recordings inside the re-pick sweep window, so this is at most one lookup per event (plus bounded
    /// retries on transient failure).</summary>
    private async Task<List<string>> NetworksForEventAsync(Event ev, CancellationToken ct)
    {
        if (ev.Broadcast is null && ev.TsdbEventId is { Length: > 0 } tid)
        {
            var now = EpochTime.Now();
            var last = _lastTvLookup.GetOrAdd(ev.Id, 0);
            if (now - last >= TvLookupRetryS && _lastTvLookup.TryUpdate(ev.Id, now, last))
            {
                var rows = await _tsdb.GetEventTvAsync(tid, ct);
                if (rows is not null)   // null = the call failed → leave Broadcast null and retry later
                {
                    var tv = string.Join(", ", rows.Select(r => CleanNetworkName(r.Channel, r.Country))
                        .Where(n => n.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(24));
                    await _gate.WriteAsync(async () =>
                    {
                        var fresh = await _db.Events.FindAsync(new object?[] { ev.Id }, ct);
                        if (fresh is not null && fresh.Broadcast is null) { fresh.Broadcast = tv; await _db.SaveChangesAsync(ct); }
                    }, ct);
                    ev.Broadcast = tv;
                    if (tv.Length > 0) _log.LogInformation("[EpgRepick] '{Title}' is listed on: {Networks}", ev.Title, tv);
                }
            }
        }
        return SplitNetworks(ev.Broadcast);
    }

    /// <summary>Drop the COUNTRY's own words from a broadcaster name ("ESPN 2 Netherlands" + "The Netherlands" →
    /// "ESPN 2") so international listings can match the user's channel names, which rarely carry the country.</summary>
    private static string CleanNetworkName(string channel, string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return channel.Trim();
        var countryTokens = TokenSet(country, minLen: 2);
        var kept = System.Text.RegularExpressions.Regex.Split(channel.Trim(), @"\s+")
            .Where(w => !countryTokens.Contains(System.Text.RegularExpressions.Regex.Replace(w.ToUpperInvariant(), "[^A-Z0-9]", "")));
        var name = string.Join(' ', kept).Trim();
        return name.Length > 1 ? name : channel.Trim();
    }

    private static List<string> SplitNetworks(string? broadcast) =>
        string.IsNullOrWhiteSpace(broadcast) ? new()
        : broadcast.Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Human-readable network list for messages, capped so an event syndicated to 20 countries doesn't
    /// flood a notification.</summary>
    private static string NetList(List<string> networks) =>
        string.Join(", ", networks.Take(6)) + (networks.Count > 6 ? ", …" : "");

    /// <summary>Whole-token network-name match, scored: 2 = every word of the network appears as a whole token of
    /// the channel name ("NBC" → "US: NBC 4 New York", never "CNBC"); 1 = only the BRAND (first word) matches
    /// ("Peacock Premium" → "US: Peacock 4K") — weaker, ranked below full matches and still guarded by the
    /// guide-corroboration check; 0 = no match.</summary>
    private static int NetworkNameMatch(string channelName, string network)
    {
        var netTokens = TokensOrdered(network, minLen: 2);
        if (netTokens.Count == 0) return 0;
        var chTokens = TokenSet(channelName, minLen: 2);
        if (netTokens.All(chTokens.Contains)) return 2;
        return chTokens.Contains(netTokens[0]) ? 1 : 0;
    }

    private static List<string> TokensOrdered(string? s, int minLen = 3)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var t in System.Text.RegularExpressions.Regex.Split(s.ToUpperInvariant(), "[^A-Z0-9]+"))
            if (t.Length >= minLen) list.Add(t);
        return list;
    }

    private static HashSet<string> TokenSet(string? s, int minLen = 3)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(s)) return set;
        foreach (var t in System.Text.RegularExpressions.Regex.Split(s.ToUpperInvariant(), "[^A-Z0-9]+"))
            if (t.Length >= minLen) set.Add(t);
        return set;
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
