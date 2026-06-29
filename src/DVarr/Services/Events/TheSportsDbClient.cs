using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DVarr.Infrastructure;
using DVarr.Services;

namespace DVarr.Services.Events;

// ---- Lightweight DTOs surfaced to the UI + ingest/import (only the fields DVarr uses) ----
public sealed record TsdbSport(string Name, string? Format);
public sealed record TsdbLeague(string Id, string Name, string Sport, string? Country, string? Alternate, string? Poster, string? Badge);
public sealed record TsdbTeam(string Id, string Name, string? Badge, string? Logo);
public sealed record TsdbEvent(string Id, string Title, long? StartUtc, bool DateOnly, string? Status,
    string? Thumb, string? Poster, int? Round, string? Season, string? League, string? Sport, string? LeagueId,
    string? HomeTeamId, string? HomeTeamName, string? AwayTeamId, string? AwayTeamName);

/// <summary>
/// Thin wrapper over TheSportsDB <b>v2</b> (premium key, sent as the <c>X-API-KEY</c> header). Backs the league
/// pickers, the team-follow team list, event sync, and the manual-recording → event match. v2 unlocks the FULL
/// season schedule (<c>/schedule/league/{id}/{season}</c>), team rosters with logos (<c>/list/teams/{id}</c>) and
/// league artwork (<c>/lookup/league/{id}</c>). Response arrays sit under varying keys (all/schedule/list/lookup),
/// so a tolerant array-finder is used. Near-static catalogue calls are cached. Public sports data only — never IPTV.
/// </summary>
public sealed class TheSportsDbClient
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<TheSportsDbClient> _log;
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    private int _httpOk;
    /// <summary>Count of successful (2xx) HTTP responses this instance has received — lets a caller distinguish
    /// "provider unreachable / all calls failed" from "reachable but no events", so a failed sync doesn't look empty.</summary>
    public int HttpOkCount => _httpOk;

    // key -> (expiresUtc, payload). Static so the cache survives this transient typed client.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset exp, object data)> _cache = new();

    public TheSportsDbClient(HttpClient http, SettingsService settings, ILogger<TheSportsDbClient> log) { _http = http; _settings = settings; _log = log; }

    // The premium v2 key (Settings → thesportsdb_api_key) is sent as the X-API-KEY header. Folded into cache keys so a
    // key change re-fetches. The public test key "3" only works on the legacy v1 URL — v2 needs a real (paid) key.
    private async Task<string> KeyAsync() { var k = await _settings.GetAsync("thesportsdb_api_key"); return string.IsNullOrWhiteSpace(k) ? "3" : k!.Trim(); }

    private async Task<T> CachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        if (_cache.TryGetValue(key, out var hit) && hit.exp > DateTimeOffset.UtcNow && hit.data is T cached)
            return cached;
        var fresh = await factory();
        if (fresh != null)
        {
            // An empty collection usually means a transient fetch failure (429/network) — cache it only briefly so
            // a real result is picked up soon, rather than serving empty for the full TTL.
            var emptyColl = fresh is System.Collections.ICollection c && c.Count == 0;
            _cache[key] = (DateTimeOffset.UtcNow.Add(emptyColl ? TimeSpan.FromMinutes(5) : ttl), fresh);
        }
        return fresh;
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        var url = $"https://www.thesportsdb.com/api/v2/json/{path}";
        var key = await KeyAsync();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-API-KEY", key); // v2 auth is the header, not the URL
                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 3)
                {
                    // Back off (honouring Retry-After when present) and retry instead of silently returning null — a
                    // dropped fixture would shift every later episode's chronological ordinal.
                    var wait = resp.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(2 * (attempt + 1));
                    if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
                    _log.LogWarning("[TSDB] {Path} → 429; backing off {Sec}s (attempt {N})", path, wait.TotalSeconds, attempt + 1);
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode) { _log.LogWarning("[TSDB] {Path} → {Code}", path, (int)resp.StatusCode); return null; }
                _httpOk++; // a real, reachable 2xx response (used to tell provider-down from empty)
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(s, default, ct);
            }
            catch (OperationCanceledException) { throw; } // shutdown/timeout — propagate, don't mask as "no data"
            catch (Exception ex) { _log.LogWarning(ex, "[TSDB] {Path} failed", path); return null; }
        }
    }

    /// <summary>v2 wraps its arrays under varying keys (all/schedule/list/lookup). Prefer the expected key, else the
    /// first array-valued property — so an endpoint key rename can't silently empty a sync.</summary>
    private static bool TryArray(JsonDocument? doc, string preferredKey, out JsonElement arr)
    {
        arr = default;
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return false;
        if (doc.RootElement.TryGetProperty(preferredKey, out var a) && a.ValueKind == JsonValueKind.Array) { arr = a; return true; }
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array) { arr = p.Value; return true; }
        return false;
    }

    // ---- Catalogue (cached) ----
    public async Task<List<TsdbSport>> GetSportsAsync(CancellationToken ct = default) => await CachedAsync($"sports:{await KeyAsync()}", TimeSpan.FromHours(24), async () =>
    {
        var list = new List<TsdbSport>();
        using var doc = await GetAsync("all/sports", ct);
        if (TryArray(doc, "all", out var arr))
            foreach (var e in arr.EnumerateArray())
            {
                var name = Str(e, "strSport");
                if (!string.IsNullOrWhiteSpace(name)) list.Add(new TsdbSport(name!, Str(e, "strFormat")));
            }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

    public async Task<List<TsdbLeague>> GetLeaguesAsync(string sport, CancellationToken ct = default)
        => await CachedAsync($"leagues:{await KeyAsync()}:{sport.ToLowerInvariant()}", TimeSpan.FromHours(24), async () =>
    {
        // v2 has no per-sport league endpoint; /all/leagues lists every league (id/name/sport/alternate, NO artwork),
        // so fetch once and filter by sport. Artwork for the chosen league comes from LookupLeagueAsync on demand.
        var all = await CachedAsync($"allleagues:{await KeyAsync()}", TimeSpan.FromHours(24), async () =>
        {
            var raw = new List<TsdbLeague>();
            using var doc = await GetAsync("all/leagues", ct);
            if (TryArray(doc, "all", out var arr))
                foreach (var e in arr.EnumerateArray())
                {
                    var id = Str(e, "idLeague"); var name = Str(e, "strLeague");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                    raw.Add(new TsdbLeague(id!, name!, Str(e, "strSport") ?? "", Str(e, "strCountry"),
                        Str(e, "strLeagueAlternate"), null, null));
                }
            return raw;
        });
        return all.Where(l => string.Equals(l.Sport, sport, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

    public async Task<TsdbLeague?> LookupLeagueAsync(string idLeague, CancellationToken ct = default)
        => await CachedAsync($"league:{await KeyAsync()}:{idLeague}", TimeSpan.FromHours(6), async () =>
    {
        using var doc = await GetAsync($"lookup/league/{Uri.EscapeDataString(idLeague)}", ct);
        if (TryArray(doc, "lookup", out var arr))
            foreach (var e in arr.EnumerateArray())
            {
                var name = Str(e, "strLeague");
                if (!string.IsNullOrWhiteSpace(name))
                    // Prefer the portrait poster; fall back to the badge for the "logo" slot.
                    return new TsdbLeague(Str(e, "idLeague") ?? idLeague, name!, Str(e, "strSport") ?? "", Str(e, "strCountry"),
                        Str(e, "strLeagueAlternate"), Str(e, "strPoster") ?? Str(e, "strFanart"), Str(e, "strBadge") ?? Str(e, "strLogo"));
            }
        return null;
    });

    /// <summary>Teams in a league (v2 /list/teams/{idLeague}) — each with a badge + logo. Drives the team-follow picker.</summary>
    public async Task<List<TsdbTeam>> GetTeamsAsync(string idLeague, CancellationToken ct = default)
        => await CachedAsync($"teams:{await KeyAsync()}:{idLeague}", TimeSpan.FromHours(6), async () =>
    {
        var list = new List<TsdbTeam>();
        using var doc = await GetAsync($"list/teams/{Uri.EscapeDataString(idLeague)}", ct);
        if (TryArray(doc, "list", out var arr))
            foreach (var e in arr.EnumerateArray())
            {
                var id = Str(e, "idTeam"); var name = Str(e, "strTeam");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new TsdbTeam(id!, name!, Str(e, "strBadge"), Str(e, "strLogo") ?? Str(e, "strBadge")));
            }
        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

    // ---- Events ----
    /// <summary>The FULL season schedule (v2 /schedule/league/{id}/{season}) — every match, with team ids. This is
    /// what makes the chronological episode ordinal correct and the manual-import list complete.</summary>
    public async Task<List<TsdbEvent>> GetSeasonEventsAsync(string idLeague, string season, CancellationToken ct = default)
    {
        var list = new List<TsdbEvent>();
        using var doc = await GetAsync($"schedule/league/{Uri.EscapeDataString(idLeague)}/{Uri.EscapeDataString(season)}", ct);
        if (TryArray(doc, "schedule", out var arr))
            foreach (var e in arr.EnumerateArray()) { var ev = MapEvent(e); if (ev != null) list.Add(ev); }
        return list;
    }

    /// <summary>Look up a single event by its TheSportsDB idEvent (v2 /lookup/event/{id}) — used by manual Import to
    /// file a staged recording onto the exact game the user picked.</summary>
    public async Task<TsdbEvent?> GetEventByIdAsync(string idEvent, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"lookup/event/{Uri.EscapeDataString(idEvent)}", ct);
        if (TryArray(doc, "lookup", out var arr))
            foreach (var e in arr.EnumerateArray()) { var ev = MapEvent(e); if (ev != null) return ev; }
        return null;
    }

    /// <summary>Best-effort fuzzy event search (v2 /search/event/{name}). Used only by the manual-recording MatchQuery
    /// enrichment, which falls back to a flat filename if this returns nothing — so a v2 search miss is harmless.</summary>
    public async Task<List<TsdbEvent>> SearchEventsAsync(string name, string? season, CancellationToken ct = default)
    {
        var list = new List<TsdbEvent>();
        var query = name.Trim().Replace(' ', '_');
        var path = $"search/event/{Uri.EscapeDataString(query)}" + (string.IsNullOrWhiteSpace(season) ? "" : $"/{Uri.EscapeDataString(season!)}");
        using var doc = await GetAsync(path, ct);
        if (TryArray(doc, "search", out var arr))
            foreach (var e in arr.EnumerateArray()) { var ev = MapEvent(e); if (ev != null) list.Add(ev); }
        return list;
    }

    private static TsdbEvent? MapEvent(JsonElement e)
    {
        var id = Str(e, "idEvent");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var (start, dateOnly) = ParseTime(e);
        return new TsdbEvent(
            id!, Str(e, "strEvent") ?? "Event", start, dateOnly, Str(e, "strStatus"),
            Str(e, "strThumb"), Str(e, "strPoster"),
            IntOrNull(Str(e, "intRound")), Str(e, "strSeason"), Str(e, "strLeague"), Str(e, "strSport"), Str(e, "idLeague"),
            Str(e, "idHomeTeam"), Str(e, "strHomeTeam"), Str(e, "idAwayTeam"), Str(e, "strAwayTeam"));
    }

    private static (long? start, bool dateOnly) ParseTime(JsonElement e)
    {
        var ts = Str(e, "strTimestamp");
        if (!string.IsNullOrWhiteSpace(ts) &&
            DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return (new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds(), false);

        var date = Str(e, "dateEvent");
        if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            var time = Str(e, "strTime");
            if (!string.IsNullOrWhiteSpace(time) && TimeSpan.TryParse(time, out var tod))
                return (new DateTimeOffset(d.Date.Add(tod), TimeSpan.Zero).ToUnixTimeSeconds(), false); // strTime is UTC
            var bne = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, EpochTime.BrisbaneOffset); // date-only → Brisbane midnight
            return (bne.ToUnixTimeSeconds(), true);
        }
        return (null, false);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    private static int? IntOrNull(string? s) => int.TryParse(s, out var n) && n > 0 ? n : null;
}
