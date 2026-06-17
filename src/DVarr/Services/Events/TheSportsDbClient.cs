using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DVarr.Infrastructure;
using DVarr.Services;

namespace DVarr.Services.Events;

// ---- Lightweight DTOs surfaced to the UI + ingest/import (only the fields DVarr uses) ----
public sealed record TsdbSport(string Name, string? Format);
public sealed record TsdbLeague(string Id, string Name, string Sport, string? Country, string? Alternate, string? Poster, string? Badge);
public sealed record TsdbEvent(string Id, string Title, long? StartUtc, bool DateOnly, string? Status,
    string? Thumb, string? Poster, int? Round, string? Season, string? League, string? Sport, string? LeagueId);

/// <summary>
/// Thin wrapper over TheSportsDB v1 (free key "3"). Backs the league pickers, event enrichment, and the
/// manual-recording → event match. The free key returns artwork (strPoster/strBadge/strThumb) and is rate-limited
/// (docs say 30/min), so the near-static catalogue calls (sports, leagues-by-sport) are cached in-memory for a day
/// and league artwork for several hours. There is no fuzzy league-name endpoint, so the UI searches the cached
/// per-sport list client-side. Public sports data only — never the IPTV provider.
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

    // The public test key "3" only exposes a small sample (Soccer + Motorsport). A user-supplied key (Settings →
    // thesportsdb_api_key) unlocks the full sports/leagues catalogue. Folded into cache keys so a key change re-fetches.
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
        var url = $"https://www.thesportsdb.com/api/v1/json/{Uri.EscapeDataString(await KeyAsync())}/{path}";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 3)
                {
                    // Free tier is ~30/min. Back off (honouring Retry-After when present) and retry instead of silently
                    // returning null — a dropped fixture would shift every later episode's chronological ordinal.
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

    // ---- Catalogue (cached) ----
    public async Task<List<TsdbSport>> GetSportsAsync(CancellationToken ct = default) => await CachedAsync($"sports:{await KeyAsync()}", TimeSpan.FromHours(24), async () =>
    {
        var list = new List<TsdbSport>();
        using var doc = await GetAsync("all_sports.php", ct);
        if (doc != null && doc.RootElement.TryGetProperty("sports", out var arr) && arr.ValueKind == JsonValueKind.Array)
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
        var list = new List<TsdbLeague>();
        // search_all_leagues.php returns FULL league objects (with artwork) under the "countries" key.
        using var doc = await GetAsync($"search_all_leagues.php?s={Uri.EscapeDataString(sport)}", ct);
        if (doc != null && doc.RootElement.TryGetProperty("countries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var id = Str(e, "idLeague"); var name = Str(e, "strLeague");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new TsdbLeague(id!, name!, Str(e, "strSport") ?? sport, Str(e, "strCountry"),
                    Str(e, "strLeagueAlternate"), Str(e, "strPoster"), Str(e, "strBadge")));
            }
        return list.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });

    public async Task<TsdbLeague?> LookupLeagueAsync(string idLeague, CancellationToken ct = default)
        => await CachedAsync($"league:{await KeyAsync()}:{idLeague}", TimeSpan.FromHours(6), async () =>
    {
        using var doc = await GetAsync($"lookupleague.php?id={Uri.EscapeDataString(idLeague)}", ct);
        if (doc != null && doc.RootElement.TryGetProperty("leagues", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var name = Str(e, "strLeague");
                if (!string.IsNullOrWhiteSpace(name))
                    return new TsdbLeague(Str(e, "idLeague") ?? idLeague, name!, Str(e, "strSport") ?? "", Str(e, "strCountry"),
                        Str(e, "strLeagueAlternate"), Str(e, "strPoster"), Str(e, "strBadge"));
            }
        return null;
    });

    // ---- Events ----
    public async Task<List<TsdbEvent>> GetSeasonEventsAsync(string idLeague, string season, CancellationToken ct = default)
    {
        var list = new List<TsdbEvent>();
        using var doc = await GetAsync($"eventsseason.php?id={Uri.EscapeDataString(idLeague)}&s={Uri.EscapeDataString(season)}", ct);
        if (doc != null && doc.RootElement.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray()) { var ev = MapEvent(e); if (ev != null) list.Add(ev); }
        return list;
    }

    /// <summary>
    /// Events for a single calendar day (UTC), filtered to one league. The free tier does NOT cap this the way
    /// eventsseason is capped (mid-2026 TheSportsDB limited eventsseason to a season's first ~5-15 matches), so
    /// calling this per-day across a horizon is how DVarr actually pulls upcoming fixtures on the free key.
    /// </summary>
    public async Task<List<TsdbEvent>> GetDayEventsAsync(string idLeague, string dateUtc, CancellationToken ct = default)
    {
        var list = new List<TsdbEvent>();
        using var doc = await GetAsync($"eventsday.php?d={Uri.EscapeDataString(dateUtc)}&l={Uri.EscapeDataString(idLeague)}", ct);
        if (doc != null && doc.RootElement.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var ev = MapEvent(e);
                if (ev != null && (ev.LeagueId is null || ev.LeagueId == idLeague)) list.Add(ev); // defensive: ignore other leagues
            }
        return list;
    }

    /// <summary>Look up a single event by its TheSportsDB idEvent (lookupevent.php) — used by manual Import to file a
    /// staged recording onto the exact game the user picked.</summary>
    public async Task<TsdbEvent?> GetEventByIdAsync(string idEvent, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"lookupevent.php?id={Uri.EscapeDataString(idEvent)}", ct);
        if (doc != null && doc.RootElement.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray()) { var ev = MapEvent(e); if (ev != null) return ev; }
        return null;
    }

    /// <summary>Fuzzy-ish match: searchevents.php returns the SINGULAR "event" key (gotcha). Optional season scope.</summary>
    public async Task<List<TsdbEvent>> SearchEventsAsync(string name, string? season, CancellationToken ct = default)
    {
        var list = new List<TsdbEvent>();
        var query = name.Trim().Replace(' ', '_');
        var path = $"searchevents.php?e={Uri.EscapeDataString(query)}" + (string.IsNullOrWhiteSpace(season) ? "" : $"&s={Uri.EscapeDataString(season!)}");
        using var doc = await GetAsync(path, ct);
        if (doc != null && doc.RootElement.TryGetProperty("event", out var arr) && arr.ValueKind == JsonValueKind.Array)
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
            IntOrNull(Str(e, "intRound")), Str(e, "strSeason"), Str(e, "strLeague"), Str(e, "strSport"), Str(e, "idLeague"));
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
