using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services;
using DVarr.Services.Events;
using DVarr.Services.Tuner;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

/// <summary>
/// The "TV API" surface (stage 1) — one aggregated home-screen feed plus a caching artwork proxy for an upcoming
/// companion client. The client is thin: ALL selection logic, TheSportsDB access and artwork resolution live here, so
/// the TV never talks to TheSportsDB directly. Everything is READ-only (no DB writes, no gate). Routes stay behind the
/// normal Basic-auth middleware (NOT exempted) — the app sends the same credential as the web UI. Timestamps are epoch
/// SECONDS everywhere (house rule). Logs use the "[Tv]" category prefix and never carry credentials/keys.
/// </summary>
public static class TvEndpoints
{
    // Recording states that mean "actively capturing right now" — used to flag an event live and to prefer the live
    // recording's channel over a merely-pending one when reporting channelName.
    private static readonly HashSet<RecordingState> ActiveStates = new()
    {
        RecordingState.Starting, RecordingState.Recording, RecordingState.Recovering,
        RecordingState.FailingOver, RecordingState.Degraded, RecordingState.Stopping, RecordingState.Finalizing,
    };

    // Split an event title into home/away on "A vs B" / "A v B" (case-insensitive). Single-entity events (F1 races,
    // fights listed as one title) don't match → (null, null), which the hero renders as a single-entity card.
    private static readonly Regex VsSplit = new(@"\s+vs?\.?\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Popular-league event fetches cached ≥15 min so a home-screen refresh can't hammer TheSportsDB (its schedule
    // endpoints aren't cached inside TheSportsDbClient). Keyed by league id + calendar year.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Exp, List<TsdbEvent> Data)> _popularCache = new();

    // Per-cache-file download locks so two concurrent art requests for the same URL don't both fetch/write it.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _artLocks = new();

    public static void MapTvApi(this WebApplication app)
    {
        // =====================================================================================================
        // GET /api/tv/home — the whole home screen in one round-trip.
        // =====================================================================================================
        app.MapGet("/api/tv/home", async (DVarrDbContext db, SettingsService settings, TheSportsDbClient tsdb,
            TunerLeaseManager leases, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("DVarr.Api.Tv");
            var now = EpochTime.Now();
            long horizon48 = now + 48 * 3600;   // hero + live/upcoming window
            long horizon7d = now + 7 * 24 * 3600; // league-chip upcoming count
            long liveFloor = now - 12 * 3600;   // don't scan ancient rows; live-now is caught by Status/EndUtc

            // ---- Followed model: monitored leagues, narrowed by the canonical team-/session-follow filter ----
            var followedLeagues = await db.Leagues.AsNoTracking().Where(l => l.Monitored).ToListAsync(ct);
            var followedLeagueIds = followedLeagues.Select(l => l.Id).ToList();
            // Reuse the canonical follow sets (same parsers the scheduler/calendar use).
            var teamSets = followedLeagues
                .Select(l => (l.Id, Set: AutoScheduleService.ParseMonitoredTeamIds(l.MonitoredTeamsJson)))
                .Where(x => x.Set.Count > 0).ToDictionary(x => x.Id, x => x.Set);
            var sessionSets = followedLeagues.Where(l => MotorsportSession.IsMotorsport(l.Sport))
                .Select(l => (l.Id, Set: AutoScheduleService.ParseMonitoredSessions(l.MonitoredSessionsJson)))
                .Where(x => x.Set.Count > 0).ToDictionary(x => x.Id, x => x.Set);

            // ---- Followed events across the widest window we need (7d), then follow-filter in memory ----
            var followedEventsRaw = followedLeagueIds.Count == 0
                ? new List<Event>()
                : await db.Events.AsNoTracking()
                    .Where(e => followedLeagueIds.Contains(e.LeagueId)
                        && e.StartUtc <= horizon7d
                        && (e.StartUtc >= liveFloor || e.Status == EventStatus.Live))
                    .OrderBy(e => e.StartUtc).Take(3000).ToListAsync(ct);
            var followedFiltered = (teamSets.Count > 0 || sessionSets.Count > 0)
                ? followedEventsRaw.Where(e => LeagueEndpoints.EventFollowed(e, teamSets, sessionSets)).ToList()
                : followedEventsRaw;

            // ---- Popular-league locals: any League row whose TSDB id is in the popular list (Monitored not required) ----
            var popularIds = ParseIdArray(await settings.GetAsync("tv_hero_popular_leagues"));
            var syncedPopular = popularIds.Count == 0
                ? new List<League>()
                : await db.Leagues.AsNoTracking()
                    .Where(l => l.ExternalLeagueId != null && popularIds.Contains(l.ExternalLeagueId)).ToListAsync(ct);
            var syncedPopByExt = syncedPopular.Where(l => l.ExternalLeagueId != null)
                .GroupBy(l => l.ExternalLeagueId!).ToDictionary(g => g.Key, g => g.First());
            var syncedPopIds = syncedPopular.Select(l => l.Id).ToList();
            var syncedPopEvents = syncedPopIds.Count == 0
                ? new List<Event>()
                : await db.Events.AsNoTracking()
                    .Where(e => syncedPopIds.Contains(e.LeagueId) && e.StartUtc <= horizon48
                        && (e.StartUtc >= liveFloor || e.Status == EventStatus.Live))
                    .OrderBy(e => e.StartUtc).Take(500).ToListAsync(ct);

            // League-name lookup for every internal event we might surface (followed + synced-popular).
            var leagueNameById = new Dictionary<int, string>();
            foreach (var l in followedLeagues) leagueNameById[l.Id] = l.Name;
            foreach (var l in syncedPopular) leagueNameById[l.Id] = l.Name;

            // ---- Recordings for every internal event in the 48h window (one query; isScheduled + channelName + live) ----
            var next48 = followedFiltered.Where(e => e.StartUtc <= horizon48).ToList();
            var internalWindowIds = next48.Select(e => e.Id).Concat(syncedPopEvents.Select(e => e.Id)).Distinct().ToList();
            var recRows = internalWindowIds.Count == 0
                ? new List<RecRow>()
                : await db.Recordings.AsNoTracking()
                    .Where(r => r.EventId != null && internalWindowIds.Contains(r.EventId.Value)
                        && r.State != RecordingState.Done && r.State != RecordingState.Missed && r.State != RecordingState.Cancelled)
                    .Select(r => new RecRow(r.EventId!.Value, r.ChannelId, r.State)).ToListAsync(ct);
            var chanIds = recRows.Select(r => r.ChannelId).Distinct().ToList();
            var chanNames = chanIds.Count == 0
                ? new Dictionary<int, string>()
                : await db.Channels.AsNoTracking().Where(c => chanIds.Contains(c.Id))
                    .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var recByEvent = recRows.GroupBy(r => r.EventId).ToDictionary(g => g.Key, g =>
            {
                RecRow? active = g.Where(x => ActiveStates.Contains(x.State)).Cast<RecRow?>().FirstOrDefault();
                var chosen = active ?? g.First();
                return (Active: active != null, ChannelName: chanNames.TryGetValue(chosen.ChannelId, out var cn) ? cn : null);
            });

            bool IsLive(Event e) => e.Status == EventStatus.Live
                || (recByEvent.TryGetValue(e.Id, out var r) && r.Active)
                || (e.EndUtc.HasValue && e.StartUtc <= now && e.EndUtc.Value >= now);

            HeroCandidate FromEvent(Event e, string source)
            {
                var (h, a) = SplitTeams(e.Title);
                return new HeroCandidate(e.Id, e.TsdbEventId, source, e.Title, e.HomeTeamId, e.AwayTeamId, h, a,
                    e.LeagueId, null, leagueNameById.GetValueOrDefault(e.LeagueId, ""),
                    e.Round is { } rd ? $"Round {rd}" : null, e.StartUtc, IsLive(e));
            }

            // ---- Live & Upcoming (followed, next 48h): live-first, then soonest ----
            var followedOrdered = next48.Where(e => IsLive(e) || e.StartUtc >= now)
                .OrderByDescending(e => IsLive(e)).ThenBy(e => e.StartUtc).ToList();
            var liveUpcoming = followedOrdered.Take(20).ToList();

            // ---- Hero: an ORDERED PIPELINE of providers (followed → popular). A watch-history provider slots in
            //      between them in a later phase — keep the concat structure so it's a one-line insertion. ----
            var seen = new HashSet<string>();
            var hero = new List<HeroCandidate>();
            void TryAddHero(HeroCandidate c) { if (hero.Count < 5 && seen.Add(DedupeKey(c))) hero.Add(c); }

            // Provider 1 — followed.
            foreach (var e in followedOrdered) { TryAddHero(FromEvent(e, "followed")); if (hero.Count >= 5) break; }

            // --- SEAM: a watch-history provider is inserted HERE in a later phase (between followed and popular). ---

            // Provider 2 — popular fill (only when followed didn't fill the 5 slots; keeps TSDB off the hot path otherwise).
            if (hero.Count < 5 && popularIds.Count > 0)
            {
                int year = EpochTime.ToDisplay(now).Year;
                foreach (var pid in popularIds)
                {
                    if (hero.Count >= 5) break;
                    if (syncedPopByExt.TryGetValue(pid, out var lg))
                    {
                        // Synced popular league → use local events (no Monitored requirement).
                        var evs = syncedPopEvents
                            .Where(e => e.LeagueId == lg.Id && e.StartUtc <= horizon48 && (IsLive(e) || e.StartUtc >= now))
                            .OrderByDescending(IsLive).ThenBy(e => e.StartUtc);
                        foreach (var e in evs) { TryAddHero(FromEvent(e, "popular")); if (hero.Count >= 5) break; }
                    }
                    else
                    {
                        // Unsynced popular league → cached TheSportsDB schedule, filtered to the 48h window.
                        var evs = await PopularEventsCachedAsync(tsdb, pid, year, ct);
                        var filt = evs
                            .Where(e => e.StartUtc is { } st && st <= horizon48 && (st >= now || TsdbLive(e, now)))
                            .OrderByDescending(e => TsdbLive(e, now)).ThenBy(e => e.StartUtc ?? long.MaxValue);
                        foreach (var e in filt) { TryAddHero(FromTsdb(e, pid, now)); if (hero.Count >= 5) break; }
                    }
                }
            }

            // ---- Recently recorded: newest library items (files that graduated from a finished recording) ----
            var libItems = await db.LibraryItems.AsNoTracking()
                .Where(i => i.Status == LibraryItemStatus.Ok)
                .OrderByDescending(i => i.CreatedUtc).Take(15).ToListAsync(ct);
            var libLeagueIds = libItems.Where(i => i.LeagueId != null).Select(i => i.LeagueId!.Value).Distinct().ToList();
            var libLeagueNames = libLeagueIds.Count == 0
                ? new Dictionary<int, string>()
                : await db.Leagues.AsNoTracking().Where(l => libLeagueIds.Contains(l.Id))
                    .ToDictionaryAsync(l => l.Id, l => l.Name, ct);

            // ---- League chips: followed leagues + count of followed events in the next 7 days ----
            var upcomingByLeague = followedFiltered
                .Where(e => e.StartUtc >= now && e.StartUtc <= horizon7d)
                .GroupBy(e => e.LeagueId).ToDictionary(g => g.Key, g => g.Count());

            // ---- Sources: in-memory lease state only (never probe the provider — that would burn a stream slot) ----
            var srcRows = await db.Sources.AsNoTracking().OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.Label, s.Enabled, s.MaxStreams }).ToListAsync(ct);
            var sources = srcRows.Select((s, i) => new
            {
                sourceId = s.Id.ToString(),
                name = string.IsNullOrWhiteSpace(s.Label) ? $"Source {i + 1}" : s.Label,
                state = s.Enabled ? "connected" : "offline",
                inUse = leases.IsFree(s.Id) ? 0 : 1,
                total = s.MaxStreams > 0 ? s.MaxStreams : 1,
            }).ToList();

            return Results.Json(new
            {
                serverTimeUtc = now,
                hero = hero.Select(c =>
                {
                    var eventRef = c.InternalEventId is { } iid ? iid.ToString() : $"tsdb-{c.TsdbEventId}";
                    var eventIdOut = c.InternalEventId is { } iid2 ? iid2.ToString() : $"tsdb:{c.TsdbEventId}";
                    string? leagueRef = c.InternalLeagueId is { } lid ? lid.ToString()
                        : (c.TsdbLeagueId is { } tl ? $"tsdb-{tl}" : null);
                    bool sched = c.InternalEventId is { } sid && recByEvent.ContainsKey(sid);
                    string? chan = c.InternalEventId is { } cid && recByEvent.TryGetValue(cid, out var rr) ? rr.ChannelName : null;
                    return new
                    {
                        eventId = eventIdOut,
                        source = c.Source,
                        title = c.Title,
                        homeTeam = c.HomeTeamName,
                        awayTeam = c.AwayTeamName,
                        league = new { id = c.InternalLeagueId?.ToString(), name = c.LeagueName, round = c.Round },
                        startUtc = c.StartUtc,
                        venue = (string?)null, // TSDB event DTOs don't carry venue today; left null
                        status = c.IsLive ? "live" : "upcoming",
                        isScheduled = sched,
                        channelName = chan,
                        blurb = (string?)null,
                        art = new
                        {
                            // hero-bg cascades server-side (event thumb → league fanart → league poster → 404).
                            background = $"/api/tv/art/hero-bg/{eventRef}",
                            homeBadge = c.HomeTeamId != null && leagueRef != null ? $"/api/tv/art/team-badge/{leagueRef}/{c.HomeTeamId}" : null,
                            awayBadge = c.AwayTeamId != null && leagueRef != null ? $"/api/tv/art/team-badge/{leagueRef}/{c.AwayTeamId}" : null,
                            // Lazy: emit the route whenever we have a team id + leagueRef; the art route resolves the
                            // cutout (and 404s if the team has none) — no TSDB call on this hot path.
                            homeCutout = c.HomeTeamId != null && leagueRef != null ? $"/api/tv/art/team-cutout/{leagueRef}/{c.HomeTeamId}" : null,
                            awayCutout = c.AwayTeamId != null && leagueRef != null ? $"/api/tv/art/team-cutout/{leagueRef}/{c.AwayTeamId}" : null,
                            leagueBadge = leagueRef != null ? $"/api/tv/art/league-badge/{leagueRef}" : null,
                        },
                    };
                }),
                liveAndUpcoming = liveUpcoming.Select(e =>
                {
                    var leagueRef = e.LeagueId.ToString();
                    bool sched = recByEvent.ContainsKey(e.Id);
                    string? chan = recByEvent.TryGetValue(e.Id, out var rr) ? rr.ChannelName : null;
                    return new
                    {
                        eventId = e.Id.ToString(),
                        title = e.Title,
                        league = leagueNameById.GetValueOrDefault(e.LeagueId, ""),
                        startUtc = e.StartUtc,
                        status = IsLive(e) ? "live" : "upcoming",
                        isScheduled = sched,
                        channelName = chan,
                        progress = (string?)null, // live match-clock is a later phase
                        art = new
                        {
                            homeBadge = e.HomeTeamId != null ? $"/api/tv/art/team-badge/{leagueRef}/{e.HomeTeamId}" : null,
                            awayBadge = e.AwayTeamId != null ? $"/api/tv/art/team-badge/{leagueRef}/{e.AwayTeamId}" : null,
                            leagueBadge = $"/api/tv/art/league-badge/{leagueRef}",
                        },
                    };
                }),
                // recordingId is the LIBRARY item id (stable string identity for the card; also keys the thumb route).
                recentlyRecorded = libItems.Select(i => new
                {
                    recordingId = i.Id.ToString(),
                    eventId = i.EventId?.ToString(),
                    title = i.Title,
                    league = i.LeagueId is { } lid && libLeagueNames.TryGetValue(lid, out var ln) ? ln : null,
                    recordedUtc = i.StartUtc,
                    durationSec = i.DurationS,
                    art = new { thumb = $"/api/library/{i.Id}/thumb" },
                    status = i.Status.ToString(),
                }),
                leagues = followedLeagues.Select(l => new
                {
                    leagueId = l.Id.ToString(),
                    name = l.Name,
                    badge = $"/api/tv/art/league-badge/{l.Id}",
                    upcomingCount = upcomingByLeague.TryGetValue(l.Id, out var uc) ? uc : 0,
                }),
                sources,
            });
        });

        // =====================================================================================================
        // GET /api/tv/art/{kind}/... — caching art proxy. The client NEVER supplies a URL: every kind is resolved
        // to a TheSportsDB CDN url server-side (no SSRF surface), downloaded once, cached on disk, served with a
        // long-lived Cache-Control. Missing/failed → 404 (the client has a composition cascade).
        // =====================================================================================================

        // league-badge/{leagueRef}, league-poster/{leagueRef}, league-fanart/{leagueRef}.
        // leagueRef = internal League.Id, OR "tsdb-{tsdbLeagueId}" for an unsynced popular league.
        app.MapGet("/api/tv/art/league-badge/{leagueRef}", (string leagueRef, HttpContext ctx, DVarrDbContext db,
                TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
            ServeLeagueArtAsync(leagueRef, LeagueArt.Badge, ctx, db, tsdb, paths, hf, lf, ct));

        app.MapGet("/api/tv/art/league-poster/{leagueRef}", (string leagueRef, HttpContext ctx, DVarrDbContext db,
                TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
            ServeLeagueArtAsync(leagueRef, LeagueArt.Poster, ctx, db, tsdb, paths, hf, lf, ct));

        app.MapGet("/api/tv/art/league-fanart/{leagueRef}", (string leagueRef, HttpContext ctx, DVarrDbContext db,
                TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
            ServeLeagueArtAsync(leagueRef, LeagueArt.Fanart, ctx, db, tsdb, paths, hf, lf, ct));

        // team-badge/{leagueRef}/{teamId} — resolved via GetTeamsAsync(externalLeagueId) (6h-cached in the client).
        app.MapGet("/api/tv/art/team-badge/{leagueRef}/{teamId}", async (string leagueRef, string teamId, HttpContext ctx,
            DVarrDbContext db, TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("DVarr.Api.Tv");
            var (_, ext) = await ResolveLeagueRefAsync(leagueRef, db);
            if (ext is null) return Results.NotFound();
            var teams = await tsdb.GetTeamsAsync(ext, ct);
            var badge = teams.FirstOrDefault(t => t.Id == teamId)?.Badge;
            return await FetchAndServeAsync(badge, ctx, paths, hf, log, ct);
        });

        // event-thumb/{eventRef} — Event.ThumbUrl, or GetEventByIdAsync(tsdbId).Thumb for a popular event.
        app.MapGet("/api/tv/art/event-thumb/{eventRef}", async (string eventRef, HttpContext ctx, DVarrDbContext db,
            TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("DVarr.Api.Tv");
            var url = await ResolveEventThumbAsync(eventRef, db, tsdb, ct);
            return await FetchAndServeAsync(url, ctx, paths, hf, log, ct);
        });

        // hero-bg/{eventRef} — the hero background cascade (event thumb → home-team fanart → league fanart →
        // league poster → 404). Resolution is lazy — it happens here, never on the /home hot path.
        app.MapGet("/api/tv/art/hero-bg/{eventRef}", async (string eventRef, HttpContext ctx, DVarrDbContext db,
            TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("DVarr.Api.Tv");
            var url = await ResolveHeroBackgroundAsync(eventRef, db, tsdb, ct);
            return await FetchAndServeAsync(url, ctx, paths, hf, log, ct);
        });

        // team-cutout/{leagueRef}/{teamId} — the first player carrying a cutout (else the first with a render),
        // via GetPlayersAsync (24h-cached). leagueRef only guards that the league is known (resolves its TSDB id);
        // players are keyed by team. None with either → 404.
        app.MapGet("/api/tv/art/team-cutout/{leagueRef}/{teamId}", async (string leagueRef, string teamId, HttpContext ctx,
            DVarrDbContext db, TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("DVarr.Api.Tv");
            var (_, ext) = await ResolveLeagueRefAsync(leagueRef, db);
            if (ext is null) return Results.NotFound();
            var players = await tsdb.GetPlayersAsync(teamId, ct);
            var url = players.FirstOrDefault(p => p.Cutout != null)?.Cutout
                ?? players.FirstOrDefault(p => p.Render != null)?.Render;
            return await FetchAndServeAsync(url, ctx, paths, hf, log, ct);
        });

        // team-fanart/{leagueRef}/{teamId} — the team's fanart, via GetTeamsAsync(externalLeagueId) (6h-cached).
        // Absent (or the team isn't in that league's list) → 404.
        app.MapGet("/api/tv/art/team-fanart/{leagueRef}/{teamId}", async (string leagueRef, string teamId, HttpContext ctx,
            DVarrDbContext db, TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("DVarr.Api.Tv");
            var (_, ext) = await ResolveLeagueRefAsync(leagueRef, db);
            if (ext is null) return Results.NotFound();
            var teams = await tsdb.GetTeamsAsync(ext, ct);
            var url = teams.FirstOrDefault(t => t.Id == teamId)?.Fanart;
            return await FetchAndServeAsync(url, ctx, paths, hf, log, ct);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Hero-selection helpers
    // ---------------------------------------------------------------------------------------------------------

    private static HeroCandidate FromTsdb(TsdbEvent e, string fallbackLeagueId, long now)
    {
        var (h, a) = SplitTeams(e.Title);
        return new HeroCandidate(null, e.Id, "popular", e.Title, e.HomeTeamId, e.AwayTeamId,
            e.HomeTeamName ?? h, e.AwayTeamName ?? a, null, e.LeagueId ?? fallbackLeagueId, e.League ?? "",
            e.Round is { } rd ? $"Round {rd}" : null, e.StartUtc ?? 0, TsdbLive(e, now));
    }

    private static bool TsdbLive(TsdbEvent e, long now)
    {
        var s = e.Status?.Trim().ToLowerInvariant();
        if (s is "1h" or "2h" or "ht" or "et" or "p" or "live" or "in play" or "in progress" or "playing") return true;
        // No reliable end time on a TSDB schedule row; treat a fixture that started within the last 4h as in-play.
        return e.StartUtc is { } st && st <= now && st >= now - 4 * 3600;
    }

    private static string DedupeKey(HeroCandidate c)
        => !string.IsNullOrEmpty(c.TsdbEventId) ? $"tsdb:{c.TsdbEventId}"
        : c.InternalEventId is { } id ? $"int:{id}"
        : $"t:{c.Title}:{c.StartUtc}";

    private static (string? Home, string? Away) SplitTeams(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, null);
        var parts = VsSplit.Split(title.Trim(), 2);
        if (parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0)
            return (parts[0].Trim(), parts[1].Trim());
        return (null, null);
    }

    private static List<string> ParseIdArray(string? json)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var s = e.ValueKind == JsonValueKind.String ? e.GetString()
                          : e.ValueKind == JsonValueKind.Number ? e.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s.Trim())) list.Add(s.Trim());
                }
        }
        catch { /* malformed → no popular fill */ }
        return list;
    }

    private static async Task<List<TsdbEvent>> PopularEventsCachedAsync(TheSportsDbClient tsdb, string leagueId, int year, CancellationToken ct)
    {
        var key = $"pop:{leagueId}:{year}";
        if (_popularCache.TryGetValue(key, out var hit) && hit.Exp > DateTimeOffset.UtcNow) return hit.Data;
        var evs = await tsdb.GetLeagueScheduleAroundAsync(leagueId, year, ct);
        _popularCache[key] = (DateTimeOffset.UtcNow.AddMinutes(15), evs);
        return evs;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Art resolution
    // ---------------------------------------------------------------------------------------------------------

    private enum LeagueArt { Badge, Poster, Fanart }

    private static async Task<(League? Local, string? Ext)> ResolveLeagueRefAsync(string leagueRef, DVarrDbContext db)
    {
        if (string.IsNullOrWhiteSpace(leagueRef)) return (null, null);
        if (leagueRef.StartsWith("tsdb-", StringComparison.OrdinalIgnoreCase))
        {
            var id = leagueRef[5..];
            return (null, string.IsNullOrWhiteSpace(id) ? null : id);
        }
        if (int.TryParse(leagueRef, out var lid))
        {
            var l = await db.Leagues.AsNoTracking().FirstOrDefaultAsync(x => x.Id == lid);
            return (l, l?.ExternalLeagueId);
        }
        return (null, null);
    }

    private static async Task<IResult> ServeLeagueArtAsync(string leagueRef, LeagueArt kind, HttpContext ctx,
        DVarrDbContext db, TheSportsDbClient tsdb, RuntimePaths paths, IHttpClientFactory hf, ILoggerFactory lf, CancellationToken ct)
    {
        var log = lf.CreateLogger("DVarr.Api.Tv");
        var (local, ext) = await ResolveLeagueRefAsync(leagueRef, db);
        string? url = kind switch
        {
            LeagueArt.Badge => local?.BadgeUrl,
            LeagueArt.Poster => local?.PosterUrl,
            _ => null,
        };
        if (url is null && ext is not null)
        {
            var m = await tsdb.LookupLeagueAsync(ext, ct);
            url = kind switch { LeagueArt.Badge => m?.Badge, LeagueArt.Poster => m?.Poster, _ => m?.Fanart };
        }
        return await FetchAndServeAsync(url, ctx, paths, hf, log, ct);
    }

    private static async Task<string?> ResolveEventThumbAsync(string eventRef, DVarrDbContext db, TheSportsDbClient tsdb, CancellationToken ct)
    {
        if (eventRef.StartsWith("tsdb-", StringComparison.OrdinalIgnoreCase))
        {
            var ev = await tsdb.GetEventByIdAsync(eventRef[5..], ct);
            return ev?.Thumb;
        }
        if (int.TryParse(eventRef, out var eid))
        {
            var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == eid, ct);
            return e?.ThumbUrl;
        }
        return null;
    }

    // Cascade: event thumb → home-team fanart (two-team events) → league fanart → league poster. Resolves the
    // event's league (for fanart/poster) and its home team id (for team fanart). Runs on the art route, not /home.
    private static async Task<string?> ResolveHeroBackgroundAsync(string eventRef, DVarrDbContext db, TheSportsDbClient tsdb, CancellationToken ct)
    {
        string? thumb;
        string? ext;         // TSDB league id for fanart/poster lookup
        string? localPoster; // a synced league already stores its poster locally
        string? homeTeamId;  // present only on a two-team event → its fanart is preferred over league art

        if (eventRef.StartsWith("tsdb-", StringComparison.OrdinalIgnoreCase))
        {
            var ev = await tsdb.GetEventByIdAsync(eventRef[5..], ct);
            thumb = ev?.Thumb; ext = ev?.LeagueId; localPoster = null; homeTeamId = ev?.HomeTeamId;
        }
        else if (int.TryParse(eventRef, out var eid))
        {
            var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == eid, ct);
            if (e is null) return null;
            thumb = e.ThumbUrl; homeTeamId = e.HomeTeamId;
            var l = await db.Leagues.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.LeagueId, ct);
            ext = l?.ExternalLeagueId; localPoster = l?.PosterUrl;
        }
        else return null;

        if (!string.IsNullOrEmpty(thumb)) return thumb;

        // Prefer the home team's fanart before falling back to league-level art (6h-cached team list).
        if (ext is not null && !string.IsNullOrWhiteSpace(homeTeamId))
        {
            var teams = await tsdb.GetTeamsAsync(ext, ct);
            var teamFanart = teams.FirstOrDefault(t => t.Id == homeTeamId)?.Fanart;
            if (!string.IsNullOrEmpty(teamFanart)) return teamFanart;
        }

        string? fanart = null, poster = localPoster;
        if (ext is not null)
        {
            var m = await tsdb.LookupLeagueAsync(ext, ct);
            fanart = m?.Fanart;
            poster ??= m?.Poster;
        }
        return fanart ?? poster;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Download + disk cache
    // ---------------------------------------------------------------------------------------------------------

    private static async Task<IResult> FetchAndServeAsync(string? cdnUrl, HttpContext ctx, RuntimePaths paths,
        IHttpClientFactory httpFactory, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cdnUrl)) return Results.NotFound();
        if (!Uri.TryCreate(cdnUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IsTsdbHost(uri))
            return Results.NotFound();

        var ext = SafeExt(uri);
        var dir = Path.Combine(paths.ConfigDir, "artcache");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Sha1Hex(cdnUrl) + ext);

        if (!File.Exists(file))
        {
            var sem = _artLocks.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct);
            try
            {
                if (!File.Exists(file) && !await DownloadAsync(cdnUrl, file, httpFactory, log, ct))
                    return Results.NotFound();
            }
            finally { sem.Release(); }
        }

        ctx.Response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(file, ContentType(ext));
    }

    private static async Task<bool> DownloadAsync(string url, string dest, IHttpClientFactory factory, ILogger log, CancellationToken ct)
    {
        const long cap = 10L * 1024 * 1024; // skip anything over 10 MB
        var tmp = dest + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var http = factory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogDebug("[Tv] art fetch {Url} -> {Code}", url, (int)resp.StatusCode);
                return false;
            }
            if (resp.Content.Headers.ContentLength is > cap)
            {
                log.LogDebug("[Tv] art skipped (>{Cap}B) {Url}", cap, url);
                return false;
            }
            var overCap = false;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > cap) { overCap = true; break; }
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (overCap) { TryDelete(tmp); return false; }
            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) { TryDelete(tmp); throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[Tv] art fetch failed {Url}", url);
            TryDelete(tmp);
            return false;
        }
    }

    private static bool IsTsdbHost(Uri u)
    {
        var h = u.Host;
        return h.Equals("thesportsdb.com", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".thesportsdb.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeExt(Uri u)
    {
        var ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" ? ext : ".img";
    }

    private static string ContentType(string ext) => ext switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    private static string Sha1Hex(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }

    // ---------------------------------------------------------------------------------------------------------
    // Internal working shapes (responses themselves use anonymous objects, per house style)
    // ---------------------------------------------------------------------------------------------------------

    private readonly record struct RecRow(int EventId, int ChannelId, RecordingState State);

    /// <summary>A normalized hero pick from any provider (followed / popular / later: watch-history). Carries both a
    /// possible internal id and a TSDB id so the output can emit the right eventId + art references.</summary>
    private sealed record HeroCandidate(
        int? InternalEventId, string? TsdbEventId, string Source, string Title,
        string? HomeTeamId, string? AwayTeamId, string? HomeTeamName, string? AwayTeamName,
        int? InternalLeagueId, string? TsdbLeagueId, string LeagueName, string? Round,
        long StartUtc, bool IsLive);
}
