using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Events;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

public static class LeagueEndpoints
{
    public static void MapLeagueApi(this WebApplication app)
    {
        // ---- Leagues ----
        app.MapGet("/api/leagues", async (DVarrDbContext db) =>
        {
            var rows = await db.Leagues.OrderBy(l => l.Sport).ThenBy(l => l.Name).ToListAsync();
            var result = new List<object>();
            foreach (var l in rows)
            {
                var events = await db.Events.CountAsync(e => e.LeagueId == l.Id);
                var maps = await db.LeagueChannelMaps.CountAsync(m => m.LeagueId == l.Id);
                result.Add(new
                {
                    l.Id, l.Sport, l.Name, l.Monitored, provider = l.EventProvider, l.ExternalLeagueId, l.IcsUrl,
                    poster = l.PosterUrl, badge = l.BadgeUrl, color = l.Color,
                    l.ScheduleHorizonDays, eventDurationOverrideS = l.EventDurationOverrideS, monitoredTeams = ParseTeams(l.MonitoredTeamsJson),
                    lastSync = l.LastEventSyncUtc, events, mappings = maps,
                });
            }
            return Results.Json(result);
        });

        // Leagues are TheSportsDB-only now: the UI sends the chosen sport + idLeague (+ display name).
        app.MapPost("/api/leagues", async (LeagueUpsert req, DVarrDbContext db, TheSportsDbClient tsdb, DbWriteGate gate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ExternalLeagueId)) return Results.BadRequest(new { error = "a TheSportsDB league must be selected" });
            var ext = req.ExternalLeagueId!.Trim();
            // Don't create a duplicate league for the same TheSportsDB id (would double-sync events + show twice in Plex).
            var dupLeague = await db.Leagues.FirstOrDefaultAsync(x => x.ExternalLeagueId == ext, ct);
            if (dupLeague is not null) return Results.Json(new { dupLeague.Id, error = "that league is already added" }, statusCode: 409);
            var now = EpochTime.Now();

            // Pull canonical name/sport + artwork from TheSportsDB so the row is complete immediately (poster shows
            // before the first event sync). Falls back to whatever the client supplied if the lookup fails.
            var meta = await tsdb.LookupLeagueAsync(req.ExternalLeagueId!.Trim(), ct);
            // Manual-id path (no name/sport supplied) that doesn't resolve = a typo'd/unknown id — reject rather than
            // persist a junk "League" row that can never sync events.
            if (meta is null && string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "No TheSportsDB league found for that id." });
            var l = new League
            {
                Sport = (meta?.Sport ?? req.Sport ?? "").Trim(),
                Name = (req.Name ?? meta?.Name ?? "League").Trim(),
                EventProvider = "thesportsdb",
                ExternalLeagueId = req.ExternalLeagueId!.Trim(),
                PosterUrl = meta?.Poster, BadgeUrl = meta?.Badge,
                Color = ValidColor(req.Color),
                ScheduleHorizonDays = req.ScheduleHorizonDays is > 0 ? req.ScheduleHorizonDays!.Value : 14,
                EventDurationOverrideS = req.EventDurationOverrideS is > 0 ? req.EventDurationOverrideS : null,
                MonitoredTeamsJson = SerializeTeams(req.MonitoredTeams),
                Monitored = req.Monitored ?? true, CreatedUtc = now,
            };
            await gate.WriteAsync(async () => { db.Leagues.Add(l); await db.SaveChangesAsync(); });
            return Results.Json(new { l.Id });
        });

        // ---- League pickers — premium v2 unlocks the FULL catalogue, so browse every sport/league live (cached 24h)
        //      instead of the bundled free-key subset (which lacked AFL/NRL etc.). ----
        app.MapGet("/api/tsdb/sports", async (TheSportsDbClient tsdb, CancellationToken ct) =>
            Results.Json((await tsdb.GetSportsAsync(ct)).Select(s => new { s.Name, s.Format })));

        app.MapGet("/api/tsdb/leagues", async (string sport, TheSportsDbClient tsdb, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(sport)) return Results.BadRequest(new { error = "sport is required" });
            return Results.Json((await tsdb.GetLeaguesAsync(sport, ct)).Select(l => new
            {
                id = l.Id, l.Name, l.Sport, l.Country, l.Alternate, l.Poster, l.Badge,
            }));
        });

        // League details (artwork + sport) AND its teams (with logos) in one call — backs the league modal's logo
        // header and the team-follow multi-select. teamSport=true (format TeamvsTeam) means the team picker applies.
        app.MapGet("/api/tsdb/league/{id}", async (string id, TheSportsDbClient tsdb, CancellationToken ct) =>
        {
            var l = await tsdb.LookupLeagueAsync(id, ct);
            if (l is null) return Results.Json(new { error = "league not found" }, statusCode: 404);
            var fmt = (await tsdb.GetSportsAsync(ct)).FirstOrDefault(s => string.Equals(s.Name, l.Sport, StringComparison.OrdinalIgnoreCase))?.Format;
            var teamSport = string.Equals(fmt, "TeamvsTeam", StringComparison.OrdinalIgnoreCase);
            var teams = new List<object>();
            if (teamSport)
                teams = (await tsdb.GetTeamsAsync(id, ct)).Select(t => (object)new { id = t.Id, t.Name, t.Badge, t.Logo }).ToList();
            return Results.Json(new { id = l.Id, l.Name, l.Sport, l.Poster, l.Badge, teamSport, teams });
        });

        app.MapPut("/api/leagues/{id:int}", async (int id, LeagueUpsert req, DVarrDbContext db, DbWriteGate gate) =>
        {
            var l = await db.Leagues.FindAsync(id);
            if (l is null) return Results.NotFound();
            // Mirror the POST guard: don't let two leagues point at the same TheSportsDB id (would double-ingest the
            // same events). Only checked when a non-empty id is supplied; clearing it (null) is always allowed.
            var newExt = string.IsNullOrWhiteSpace(req.ExternalLeagueId) ? null : req.ExternalLeagueId!.Trim();
            if (newExt != null && await db.Leagues.AnyAsync(x => x.Id != id && x.ExternalLeagueId == newExt))
                return Results.Json(new { error = "another league is already linked to that TheSportsDB id" }, statusCode: 409);
            await gate.WriteAsync(async () =>
            {
                if (!string.IsNullOrWhiteSpace(req.Name)) l.Name = req.Name!.Trim();
                if (req.Sport != null) l.Sport = req.Sport.Trim();
                if (!string.IsNullOrWhiteSpace(req.Provider)) l.EventProvider = req.Provider!.Trim();
                l.ExternalLeagueId = newExt;
                // Legacy ICS field: only update when a valid absolute http(s) URL is supplied (SSRF guard); a normal
                // edit (which omits icsUrl) preserves the existing value rather than nulling it.
                if (req.IcsUrl != null && Uri.TryCreate(req.IcsUrl.Trim(), UriKind.Absolute, out var iu) && (iu.Scheme == Uri.UriSchemeHttp || iu.Scheme == Uri.UriSchemeHttps))
                    l.IcsUrl = req.IcsUrl.Trim();
                if (req.ScheduleHorizonDays is > 0) l.ScheduleHorizonDays = req.ScheduleHorizonDays!.Value;
                if (req.Monitored.HasValue) l.Monitored = req.Monitored.Value;
                if (req.Color != null) l.Color = ValidColor(req.Color);
                if (req.EventDurationOverrideS.HasValue) l.EventDurationOverrideS = req.EventDurationOverrideS > 0 ? req.EventDurationOverrideS : null; // 0/blank clears, >0 sets
                if (req.MonitoredTeams != null) l.MonitoredTeamsJson = SerializeTeams(req.MonitoredTeams); // sending [] = follow all teams; omitting leaves unchanged
                await db.SaveChangesAsync();
            });
            return Results.Json(new { l.Id, updated = true });
        });

        app.MapDelete("/api/leagues/{id:int}", async (int id, DVarrDbContext db, DbWriteGate gate) =>
        {
            var l = await db.Leagues.FindAsync(id);
            if (l is null) return Results.NotFound();
            await gate.WriteAsync(async () =>
            {
                // Cancel not-yet-started recordings for this league's events before the events cascade away, so a
                // deleted league can't leave an orphaned Pending/Conflict recording that still fires on the old channel.
                var evIds = await db.Events.Where(e => e.LeagueId == id).Select(e => e.Id).ToListAsync();
                if (evIds.Count > 0)
                    await db.Recordings.Where(r => r.EventId != null && evIds.Contains(r.EventId.Value) && (r.State == RecordingState.Pending || r.State == RecordingState.Conflict))
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, RecordingState.Cancelled).SetProperty(r => r.FailureReason, "league deleted"));
                db.Leagues.Remove(l); // events + mappings cascade
                await db.SaveChangesAsync();
            });
            return Results.Json(new { deleted = true });
        });

        app.MapPost("/api/leagues/{id:int}/sync", async (int id, EventIngestService ingest, CancellationToken ct) =>
        {
            var r = await ingest.SyncLeagueAsync(id, ct);
            return r.Ok ? Results.Json(r) : Results.Json(r, statusCode: 502);
        });

        // Re-resolve ALL this league's scheduled (Pending/Conflict) recordings against the current channel mapping —
        // one click after you re-pin a league's channel, instead of deleting each recording so the scheduler recreates
        // it. Updates channel/source/stream + the same-credential fallback ladder in place. Never touches a live capture.
        app.MapPost("/api/leagues/{id:int}/reresolve", async (int id, DVarrDbContext db, DbWriteGate gate, ResolverService resolver) =>
        {
            var evIds = await db.Events.Where(e => e.LeagueId == id).Select(e => e.Id).ToListAsync();
            if (evIds.Count == 0) return Results.Json(new { ok = true, updated = 0, changed = 0 });
            var recs = await db.Recordings
                .Where(r => r.EventId != null && evIds.Contains(r.EventId.Value)
                            && (r.State == RecordingState.Pending || r.State == RecordingState.Conflict))
                .ToListAsync();
            // Resolve (reads) up-front, then apply all mutations in a single write so the gate is held only briefly.
            var plans = new List<(Data.Entities.Recording R, ResolvedChannel P, List<ResolvedChannel> Fbs)>();
            foreach (var r in recs)
            {
                var res = await resolver.ResolveAsync(r.EventId!.Value);
                if (res.Ok && res.Primary is not null) plans.Add((r, res.Primary, res.Fallbacks));
            }
            var now = EpochTime.Now();
            var updated = 0; var changed = 0;
            await gate.WriteAsync(async () =>
            {
                foreach (var (r, p, fbs) in plans)
                {
                    if (p.ChannelId != r.ChannelId) changed++;
                    // SourceId is part of the (Id, SourceId) alternate key — re-point via RecordingRepoint (delete old
                    // fallbacks + tracker-bypassing UPDATE), then rebuild this recording's same-credential ladder.
                    await RecordingRepoint.ApplyAsync(db, r.Id, p.SourceId, p.ChannelId, p.StreamId, now);
                    var rank = 2; // rank 1 is the primary (carried on Recording.ChannelId); RecorderService loads fallbacks at Rank >= 2
                    foreach (var fb in fbs.Where(f => f.ChannelId != p.ChannelId))
                        db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = r.Id, Rank = rank++, ChannelId = fb.ChannelId, SourceId = fb.SourceId });
                    updated++;
                }
                await db.SaveChangesAsync();
            });
            return Results.Json(new { ok = true, updated, changed });
        });

        // ---- Events ----
        app.MapGet("/api/events", async (DVarrDbContext db, int? leagueId, long? from, long? to) =>
        {
            var q = from e in db.Events
                    join l in db.Leagues on e.LeagueId equals l.Id
                    select new { e, league = l.Name, sport = l.Sport, color = l.Color };
            if (leagueId is > 0) q = q.Where(x => x.e.LeagueId == leagueId);
            if (from is > 0) q = q.Where(x => x.e.StartUtc >= from);
            if (to is > 0) q = q.Where(x => x.e.StartUtc <= to);
            var rows = await q.OrderBy(x => x.e.StartUtc).Take(2000).ToListAsync();
            return Results.Json(rows.Select(x => new
            {
                x.e.Id, x.e.Title, x.league, x.sport, x.color, x.e.LeagueId,
                start = x.e.StartUtc, end = x.e.EndUtc, dateOnly = x.e.StartIsDateOnly,
                status = x.e.Status.ToString(), x.e.Monitored, x.e.MonitoredLocked,
            }));
        });

        // Manual event create (works for any league; keeps a stable per-league natural key).
        app.MapPost("/api/events", async (EventCreate req, DVarrDbContext db, DbWriteGate gate) =>
        {
            var l = await db.Leagues.FindAsync(req.LeagueId);
            if (l is null) return Results.BadRequest(new { error = "league not found" });
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title required" });
            var now = EpochTime.Now();
            var ev = new Event
            {
                LeagueId = l.Id, NaturalKey = $"{l.Id}:manual:{Guid.NewGuid():N}", Title = req.Title!.Trim(),
                StartUtc = req.StartUtc, EndUtc = req.EndUtc, Status = EventStatus.Scheduled,
                Monitored = req.Monitored ?? true, LastSeenSyncUtc = now,
            };
            await gate.WriteAsync(async () => { db.Events.Add(ev); await db.SaveChangesAsync(); });
            return Results.Json(new { ev.Id });
        });

        app.MapPut("/api/events/{id:int}/monitor", async (int id, MonitorReq req, DVarrDbContext db, DbWriteGate gate) =>
        {
            var ev = await db.Events.FindAsync(id);
            if (ev is null) return Results.NotFound();
            await gate.WriteAsync(async () =>
            {
                ev.Monitored = req.Monitored;
                ev.MonitoredLocked = true; // user intent is durable; no sync can override (bug #4)
                await db.SaveChangesAsync();
            });
            return Results.Json(new { ev.Id, ev.Monitored });
        });

        app.MapDelete("/api/events/{id:int}", async (int id, DVarrDbContext db, DbWriteGate gate) =>
        {
            var ev = await db.Events.FindAsync(id);
            if (ev is null) return Results.NotFound();
            await gate.WriteAsync(async () =>
            {
                // Cancel any not-yet-started recording for this event so deleting the event doesn't leave a stranded
                // Pending/Conflict recording that still fires (only those two — never touch an in-progress capture).
                await db.Recordings.Where(r => r.EventId == id && (r.State == RecordingState.Pending || r.State == RecordingState.Conflict))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, RecordingState.Cancelled).SetProperty(r => r.FailureReason, "event deleted"));
                db.Events.Remove(ev);
                await db.SaveChangesAsync();
            });
            return Results.Json(new { deleted = true });
        });

        app.MapGet("/api/events/{id:int}/resolve", async (int id, ResolverService resolver, CancellationToken ct) =>
        {
            var r = await resolver.ResolveAsync(id, ct);
            return Results.Json(new
            {
                r.Ok, r.Reason, r.Confidence,
                primary = r.Primary is null ? null : new { r.Primary.ChannelId, r.Primary.ChannelName, r.Primary.SourceId, r.Primary.Score },
                fallbacks = r.Fallbacks.Select(f => new { f.ChannelId, f.ChannelName, f.SourceId }),
            });
        });

        // ---- League ↔ channel mappings (pin a channel for a league) ----
        app.MapGet("/api/mappings", async (DVarrDbContext db, int? leagueId) =>
        {
            var q = from m in db.LeagueChannelMaps
                    join c in db.Channels on m.ChannelId equals c.Id into cj
                    from c in cj.DefaultIfEmpty()
                    join s in db.Sources on m.SourceId equals s.Id into sj
                    from s in sj.DefaultIfEmpty()
                    select new { m, channel = c != null ? c.Name : null, source = s != null ? s.Label : null };
            if (leagueId is > 0) q = q.Where(x => x.m.LeagueId == leagueId);
            var rows = await q.OrderBy(x => x.m.LeagueId).ThenBy(x => x.m.Rank).ToListAsync();
            return Results.Json(rows.Select(x => new { x.m.Id, x.m.LeagueId, x.m.ChannelId, x.channel, x.m.SourceId, x.source, x.m.Rank, x.m.Pinned }));
        });

        app.MapPost("/api/mappings", async (MappingCreate req, DVarrDbContext db, DbWriteGate gate) =>
        {
            var ch = await db.Channels.FindAsync(req.ChannelId);
            if (ch is null) return Results.BadRequest(new { error = "channel not found" });
            if (await db.Leagues.FindAsync(req.LeagueId) is null) return Results.BadRequest(new { error = "league not found" });
            if (await db.LeagueChannelMaps.AnyAsync(x => x.LeagueId == req.LeagueId && x.ChannelId == ch.Id))
                return Results.Json(new { error = "that channel is already mapped to this league" }, statusCode: 409);
            var m = new LeagueChannelMap
            {
                LeagueId = req.LeagueId, ChannelId = ch.Id, SourceId = ch.SourceId, // denormalised owning credential
                Rank = req.Rank is > 0 ? req.Rank!.Value : 1, Pinned = req.Pinned ?? true,
            };
            await gate.WriteAsync(async () => { db.LeagueChannelMaps.Add(m); await db.SaveChangesAsync(); });
            return Results.Json(new { m.Id });
        });

        app.MapDelete("/api/mappings/{id:int}", async (int id, DVarrDbContext db, DbWriteGate gate) =>
        {
            var m = await db.LeagueChannelMaps.FindAsync(id);
            if (m is null) return Results.NotFound();
            await gate.WriteAsync(async () => { db.LeagueChannelMaps.Remove(m); await db.SaveChangesAsync(); });
            return Results.Json(new { deleted = true });
        });
    }

    // Only persist a strict #rrggbb hex (the API is the trust boundary — it's interpolated into a style attr client-side).
    private static string? ValidColor(string? c)
        => !string.IsNullOrWhiteSpace(c) && System.Text.RegularExpressions.Regex.IsMatch(c.Trim(), "^#[0-9a-fA-F]{6}$") ? c.Trim() : null;

    // Team-follow: store/return the chosen teams as a compact JSON array of {id,name}. An empty/absent list = all teams.
    private static string? SerializeTeams(List<TeamRef>? teams)
    {
        if (teams is null) return null;
        var clean = teams.Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => new { id = t.Id.Trim(), name = t.Name?.Trim() }).ToList();
        return clean.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(clean);
    }
    private static object[] ParseTeams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<object>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                return doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty("id", out _))
                    .Select(e => (object)new { id = e.GetProperty("id").GetString(), name = e.TryGetProperty("name", out var n) ? n.GetString() : null })
                    .ToArray();
        }
        catch { /* malformed → treat as no teams (all) */ }
        return Array.Empty<object>();
    }
}

public sealed record TeamRef(string Id, string? Name);
public sealed record LeagueUpsert(string? Sport, string? Name, string? Provider, string? ExternalLeagueId, string? IcsUrl, int? ScheduleHorizonDays, bool? Monitored, string? Color, int? EventDurationOverrideS, List<TeamRef>? MonitoredTeams);
public sealed record EventCreate(int LeagueId, string? Title, long StartUtc, long? EndUtc, bool? Monitored);
public sealed record MonitorReq(bool Monitored);
public sealed record MappingCreate(int LeagueId, int ChannelId, int? Rank, bool? Pinned);
