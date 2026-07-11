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
/// <summary>One in-play event from v2 /livescore/{sport}: Status = strStatus (1H/HT/2H/ET/P …),
/// Progress = strProgress (the match minute). Presence in the livescore feed itself means "in play".</summary>
public sealed record TsdbLiveScore(string EventId, string? Status, string? Progress);

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

    // The premium v2 key is sent as the X-API-KEY header. A user-entered key (Settings → thesportsdb_api_key) always
    // wins; otherwise the key BUNDLED with the build is used, so the official image works out of the box with no
    // sign-up. Folded into cache keys so a key change re-fetches. The public test key "3" only works on the legacy v1
    // URL and 401s on v2, so treat both "" and the legacy "3" as "no key entered" — those fall through to the bundled
    // key (this also auto-clears a "3" left over from a v1.18 DB). With neither, GetAsync fails loudly with a clear
    // message instead of firing doomed requests that look like "provider unreachable".
    private async Task<string> KeyAsync()
    {
        var k = (await _settings.GetAsync("thesportsdb_api_key"))?.Trim();
        return !string.IsNullOrEmpty(k) && k != "3" ? k : _bundledKey.Value;
    }

    // The bundled key ships INSIDE the image, never in the repo or the UI: the GHCR publish workflow passes it as a
    // BuildKit secret and the Dockerfile writes it base64-encoded to tsdb.key beside the app binaries (so it appears
    // in neither `docker inspect` env output nor image history). DVARR_TSDB_API_KEY overrides for source/dev runs.
    // It is deliberately kept OUT of the Settings table so GET /api/settings can never surface it.
    private static readonly Lazy<string> _bundledKey = new(() =>
    {
        var env = Environment.GetEnvironmentVariable("DVARR_TSDB_API_KEY")?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "tsdb.key");
            if (File.Exists(path))
            {
                var raw = File.ReadAllText(path).Trim();
                if (raw.Length > 0)
                    return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw)).Trim();
            }
        }
        catch { /* unreadable/corrupt bundled key file → behave as if none was shipped */ }
        return "";
    });

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
        if (key.Length == 0)
        {
            // No key at all — this build shipped without a bundled key AND none was entered in Settings; every v2 call
            // would 401. Don't fire it; surface a clear, actionable reason (rather than a generic 4xx that reads like a
            // transient outage). HttpOkCount stays 0 → sync reports failure.
            _log.LogWarning("[TSDB] {Path} skipped — this build has no bundled TheSportsDB key and no key is set (Settings → TheSportsDB API key); v2 requires a premium key", path);
            return null;
        }
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
                if (!resp.IsSuccessStatusCode)
                {
                    // 401/403 on v2-with-a-header almost always means a bad/expired key — say so, don't just log the code.
                    if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        _log.LogError("[TSDB] {Path} → {Code}: authentication failed — check the TheSportsDB API key in Settings", path, (int)resp.StatusCode);
                    else _log.LogWarning("[TSDB] {Path} → {Code}", path, (int)resp.StatusCode);
                    return null;
                }
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                var doc = await JsonDocument.ParseAsync(s, default, ct);
                // A 200 can still carry a structured error body ({ "error": ... }); treat that as a failure — not a
                // reachable success — so TryArray can't latch onto an unrelated array and HttpOkCount stays honest.
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err)
                    && err.ValueKind is JsonValueKind.String or JsonValueKind.Object)
                {
                    _log.LogWarning("[TSDB] {Path} → error body: {Error}", path, err.ToString());
                    doc.Dispose();
                    return null;
                }
                _httpOk++; // a real, reachable 2xx response (used to tell provider-down from empty)
                return doc;
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
    {
        var key = await KeyAsync(); // resolve once so the outer (per-sport) and inner (all-leagues) cache keys can't disagree mid-flight
        return await CachedAsync($"leagues:{key}:{sport.ToLowerInvariant()}", TimeSpan.FromHours(24), async () =>
        {
            // v2 has no per-sport league endpoint; /all/leagues lists every league (id/name/sport/alternate, NO artwork),
            // so fetch once and filter by sport. Artwork for the chosen league comes from LookupLeagueAsync on demand.
            var all = await CachedAsync($"allleagues:{key}", TimeSpan.FromHours(24), async () =>
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
    }

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

    /// <summary>Currently in-play events for a sport (v2 /livescore/{sport}) — feeds the smart auto-stop's
    /// "still in play?" check. NOT cached (live data; the caller rate-limits). Any failure → empty list, so a
    /// livescore outage can only ever degrade auto-stop to its event-lookup path, never break a recording.</summary>
    public async Task<List<TsdbLiveScore>> GetLiveScoresAsync(string sport, CancellationToken ct = default)
    {
        var list = new List<TsdbLiveScore>();
        using var doc = await GetAsync($"livescore/{Uri.EscapeDataString(sport.Trim().ToLowerInvariant())}", ct);
        if (TryArray(doc, "livescore", out var arr))
            foreach (var e in arr.EnumerateArray())
            {
                var id = Str(e, "idEvent");
                if (!string.IsNullOrWhiteSpace(id)) list.Add(new TsdbLiveScore(id!, Str(e, "strStatus"), Str(e, "strProgress")));
            }
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
            return (EpochTime.DisplayMidnightUtc(d.Year, d.Month, d.Day), true); // date-only → display-zone midnight
        }
        return (null, false);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    private static int? IntOrNull(string? s) => int.TryParse(s, out var n) && n > 0 ? n : null;
}
