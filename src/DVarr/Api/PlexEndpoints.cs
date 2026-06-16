using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVarr.Api;

/// <summary>
/// A Plex Custom Metadata Provider (PMS 1.43.0+), the modern replacement for legacy .bundle agents — same shape
/// Sportarr exposes at sportarr.net/plex. The user adds DVarr's URL in Plex (Settings → Metadata Agents), then sets
/// a TV-Shows library to the DVarr agent: league = show, year = season, event = episode, with TheSportsDB artwork.
/// Contract (reverse-engineered from the reference): GET /plex 302→manifest; manifest declares types 2/3/4 + the
/// match + metadata feature URLs; POST .../matches; GET .../metadata/{ratingKey}; GET .../metadata/{ratingKey}/children.
/// All responses are Plex MediaContainer JSON. Public sports metadata only — never the IPTV provider.
/// </summary>
public static class PlexEndpoints
{
    private const string Identifier = "tv.plex.agents.custom.dvarr.sports";
    private const string ProviderPath = "/api/plex/provider/sports";

    // Plex expects EXACT casing (MediaProvider/MediaContainer/Metadata/Image/Genre are PascalCase; scalar fields
    // are camelCase) — so emit anonymous-object property names verbatim (no Web camelCase policy) and drop nulls.
    private static readonly JsonSerializerOptions PlexJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapPlexApi(this WebApplication app)
    {
        // Discovery: the URL the user pastes into Plex. 302 → the JSON manifest (mirrors the reference impl).
        app.MapGet("/plex", (HttpContext ctx) => Results.Redirect($"{Origin(ctx)}{ProviderPath}"));

        // Manifest.
        app.MapGet(ProviderPath, (HttpContext ctx) =>
        {
            var meta = $"{Origin(ctx)}{ProviderPath}/library/metadata";
            return Results.Json(new
            {
                MediaProvider = new
                {
                    identifier = Identifier,
                    title = "DVarr Sports Metadata",
                    version = "1.0.0",
                    protocols = "tv.plex.provider.metadata",
                    Types = new object[]
                    {
                        new { type = 2, Scheme = new[] { new { scheme = Identifier } } }, // show
                        new { type = 3, Scheme = new[] { new { scheme = Identifier } } }, // season
                        new { type = 4, Scheme = new[] { new { scheme = Identifier } } }, // episode
                    },
                    Feature = new object[]
                    {
                        new { type = "metadata", key = meta },
                        new { type = "match", key = $"{meta}/matches" },
                    },
                },
            }, PlexJson);
        });

        // Match / search — POST with Plex hints (type, title, grandparentTitle, parentIndex, index).
        app.MapPost($"{ProviderPath}/library/metadata/matches", async (HttpContext ctx, DVarrDbContext db) =>
        {
            var origin = Origin(ctx);
            JsonElement body;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Container(Array.Empty<object>()); }

            var type = GetInt(body, "type") ?? 2;
            var title = GetStr(body, "title");
            var grandparent = GetStr(body, "grandparentTitle");
            var parentIndex = GetInt(body, "parentIndex");
            var index = GetInt(body, "index");

            var matches = new List<object>();
            if (type == 2) // show
            {
                foreach (var l in await MatchLeaguesAsync(db, title)) matches.Add(await ShowAsync(db, l, origin));
            }
            else if (type == 3) // season
            {
                var l = (await MatchLeaguesAsync(db, grandparent ?? title)).FirstOrDefault();
                if (l != null && (parentIndex ?? index) is { } yr)
                {
                    var ev = await SeasonEventsAsync(db, l.Id, yr);
                    if (ev.Count > 0) matches.Add(Season(l, yr, origin));
                }
            }
            else if (type == 4) // episode
            {
                var l = (await MatchLeaguesAsync(db, grandparent)).FirstOrDefault();
                if (l != null && parentIndex is { } yr)
                {
                    var evs = await SeasonEventsAsync(db, l.Id, yr);
                    Event? hit = null;
                    if (index is { } ix && ix >= 1 && ix <= evs.Count) hit = evs[ix - 1];      // ordinal
                    hit ??= evs.FirstOrDefault(e => TitleMatch(e.Title, title));                 // fall back to title
                    if (hit != null) matches.Add(Episode(hit, l, yr, evs.IndexOf(hit) + 1, origin));
                }
            }
            return Container(matches);
        });

        // Single item by ratingKey (no comma-batch; mirrors reference).
        app.MapGet($"{ProviderPath}/library/metadata/{{ratingKey}}", async (string ratingKey, HttpContext ctx, DVarrDbContext db) =>
        {
            var origin = Origin(ctx);
            var item = await ResolveAsync(db, ratingKey, origin);
            return item is null ? NotFound() : Container(new[] { item });
        });

        // Children: show → seasons; season → episodes. Paged via X-Plex-Container-Start / -Size.
        app.MapGet($"{ProviderPath}/library/metadata/{{ratingKey}}/children", async (string ratingKey, HttpContext ctx, DVarrDbContext db) =>
        {
            var origin = Origin(ctx);
            var (start, size) = Paging(ctx);

            if (TryParse(ratingKey, "dvarr-show-", out var leagueId))
            {
                var l = await db.Leagues.FindAsync(leagueId);
                if (l is null) return NotFound();
                var years = (await db.Events.Where(e => e.LeagueId == leagueId).Select(e => e.StartUtc).ToListAsync())
                    .Select(s => EpochTime.ToBrisbane(s).Year).Distinct().OrderByDescending(y => y).ToList();
                var page = years.Skip(start).Take(size).Select(y => Season(l, y, origin)).ToList();
                return Container(page, years.Count, start);
            }
            if (TryParseSeason(ratingKey, out var lId, out var year))
            {
                var l = await db.Leagues.FindAsync(lId);
                if (l is null) return NotFound();
                var evs = await SeasonEventsAsync(db, lId, year);
                var page = evs.Skip(start).Take(size).Select((e, i) => Episode(e, l, year, start + i + 1, origin)).ToList();
                return Container(page, evs.Count, start);
            }
            return Container(Array.Empty<object>());
        });
    }

    // ---- DVarr entity → Plex object mapping ----
    private static async Task<object> ShowAsync(DVarrDbContext db, League l, string origin)
    {
        var count = await db.Events.CountAsync(e => e.LeagueId == l.Id);
        return Show(l, origin, count);
    }

    private static object Show(League l, string origin, int childCount) => new
    {
        ratingKey = $"dvarr-show-{l.Id}",
        key = $"{origin}{ProviderPath}/library/metadata/dvarr-show-{l.Id}",
        guid = $"{Identifier}://show/dvarr-show-{l.Id}",
        type = "show",
        title = l.Name,
        summary = $"{l.Sport} — {l.Name}".Trim(' ', '—'),
        studio = "DVarr",
        thumb = l.PosterUrl, art = l.PosterUrl, banner = l.BadgeUrl,
        childCount,
        Genre = string.IsNullOrWhiteSpace(l.Sport) ? Array.Empty<object>() : new object[] { new { tag = l.Sport } },
        Image = Images("coverPoster", l.PosterUrl, l.BadgeUrl, l.Name),
    };

    private static object Season(League l, int year, string origin) => new
    {
        ratingKey = $"dvarr-season-{l.Id}-{year}",
        key = $"{origin}{ProviderPath}/library/metadata/dvarr-season-{l.Id}-{year}",
        guid = $"{Identifier}://season/dvarr-season-{l.Id}-{year}",
        type = "season",
        parentType = "show",
        parentRatingKey = $"dvarr-show-{l.Id}",
        parentKey = $"{origin}{ProviderPath}/library/metadata/dvarr-show-{l.Id}",
        parentGuid = $"{Identifier}://show/dvarr-show-{l.Id}",
        title = $"{year}",
        parentTitle = l.Name,
        index = year,
        thumb = l.PosterUrl, art = l.PosterUrl,
        Image = Images("coverPoster", l.PosterUrl, l.PosterUrl, $"{l.Name} {year}"),
    };

    private static object Episode(Event e, League l, int year, int epIndex, string origin)
    {
        var thumb = string.IsNullOrWhiteSpace(e.ThumbUrl) ? l.PosterUrl : e.ThumbUrl;
        var aired = EpochTime.ToBrisbane(e.StartUtc).ToString("yyyy-MM-dd");
        return new
        {
            ratingKey = $"dvarr-episode-{e.Id}",
            key = $"{origin}{ProviderPath}/library/metadata/dvarr-episode-{e.Id}",
            guid = $"{Identifier}://episode/dvarr-episode-{e.Id}",
            type = "episode",
            parentType = "season",
            grandparentType = "show",
            title = e.Title,
            parentTitle = $"{year}",
            grandparentTitle = l.Name,
            // Per-season ordinal (by start) — unique even when motorsport sessions share an intRound, so Plex
            // never collapses FP1/Quali/Race into one episode. Must match MediaImportService's SxxExx numbering.
            index = epIndex,
            parentIndex = year,
            originallyAvailableAt = aired,
            year,
            thumb, art = thumb,
            parentRatingKey = $"dvarr-season-{l.Id}-{year}",
            grandparentRatingKey = $"dvarr-show-{l.Id}",
            grandparentGuid = $"{Identifier}://show/dvarr-show-{l.Id}",
            grandparentKey = $"{origin}{ProviderPath}/library/metadata/dvarr-show-{l.Id}",
            Image = Images("snapshot", thumb, thumb, e.Title),
        };
    }

    private static async Task<object?> ResolveAsync(DVarrDbContext db, string ratingKey, string origin)
    {
        if (TryParse(ratingKey, "dvarr-show-", out var leagueId))
        {
            var l = await db.Leagues.FindAsync(leagueId);
            return l is null ? null : await ShowAsync(db, l, origin);
        }
        if (TryParseSeason(ratingKey, out var lId, out var year))
        {
            var l = await db.Leagues.FindAsync(lId);
            return l is null ? null : Season(l, year, origin);
        }
        if (TryParse(ratingKey, "dvarr-episode-", out var eventId))
        {
            var e = await db.Events.FindAsync(eventId);
            if (e is null) return null;
            var l = await db.Leagues.FindAsync(e.LeagueId);
            if (l is null) return null;
            var yr = EpochTime.ToBrisbane(e.StartUtc).Year;
            var evs = await SeasonEventsAsync(db, l.Id, yr);
            var idx = evs.FindIndex(x => x.Id == e.Id);
            return Episode(e, l, yr, idx >= 0 ? idx + 1 : 1, origin);
        }
        return null;
    }

    // ---- helpers ----
    private static async Task<List<League>> MatchLeaguesAsync(DVarrDbContext db, string? title)
    {
        var all = await db.Leagues.ToListAsync();
        if (string.IsNullOrWhiteSpace(title)) return all.Take(20).ToList();
        var t = Norm(title);
        return all.Where(l => { var n = Norm(l.Name); return n == t || n.Contains(t) || t.Contains(n); })
                  .OrderBy(l => Math.Abs(Norm(l.Name).Length - t.Length)).Take(10).ToList();
    }

    private static async Task<List<Event>> SeasonEventsAsync(DVarrDbContext db, int leagueId, int year)
    {
        // ThenBy(Id): stable tie-break so this ordinal matches MediaImportService's on-disk SxxExx exactly.
        var all = await db.Events.Where(e => e.LeagueId == leagueId).OrderBy(e => e.StartUtc).ThenBy(e => e.Id).ToListAsync();
        return all.Where(e => EpochTime.ToBrisbane(e.StartUtc).Year == year).ToList();
    }

    private static bool TitleMatch(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Norm(a).Contains(Norm(b)) || (!string.IsNullOrWhiteSpace(b) && Norm(b!).Contains(Norm(a ?? "")));

    private static string Norm(string s) => new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

    private static object[] Images(string posterType, string? poster, string? bg, string alt)
    {
        var list = new List<object>();
        if (!string.IsNullOrWhiteSpace(poster)) list.Add(new { type = posterType, url = poster, alt });
        if (!string.IsNullOrWhiteSpace(bg)) list.Add(new { type = "background", url = bg, alt });
        return list.ToArray();
    }

    private static IResult Container(IReadOnlyCollection<object> items, int? totalSize = null, int offset = 0)
        => Results.Json(new { MediaContainer = new { size = items.Count, totalSize = totalSize ?? items.Count, offset, Metadata = items } }, PlexJson);

    private static IResult NotFound()
        => Results.Json(new { error = new { code = "not_found", message = "metadata not found" } }, PlexJson, statusCode: 404);

    private static string Origin(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    private static (int start, int size) Paging(HttpContext ctx)
    {
        int Read(string key, int def)
        {
            var v = ctx.Request.Query[key].FirstOrDefault() ?? ctx.Request.Headers[key].FirstOrDefault();
            return int.TryParse(v, out var n) && n >= 0 ? n : def;
        }
        return (Read("X-Plex-Container-Start", 0), Math.Clamp(Read("X-Plex-Container-Size", 50), 1, 500));
    }

    private static bool TryParse(string ratingKey, string prefix, out int id)
    {
        id = 0;
        return ratingKey.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(ratingKey[prefix.Length..], out id);
    }

    private static bool TryParseSeason(string ratingKey, out int leagueId, out int year)
    {
        leagueId = 0; year = 0;
        const string p = "dvarr-season-";
        if (!ratingKey.StartsWith(p, StringComparison.Ordinal)) return false;
        var rest = ratingKey[p.Length..].Split('-');
        return rest.Length == 2 && int.TryParse(rest[0], out leagueId) && int.TryParse(rest[1], out year);
    }

    private static int? GetInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static string? GetStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
