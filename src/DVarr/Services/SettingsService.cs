using System.Text.Json;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services;

/// <summary>
/// Typed configuration backed by the Settings table (docs/05 §1.7). Defaults are stated
/// identically to the plan; missing keys are seeded on startup. Writes go through the
/// single-writer gate.
/// </summary>
public sealed class SettingsService
{
    private readonly DVarrDbContext _db;
    private readonly DbWriteGate _gate;

    public SettingsService(DVarrDbContext db, DbWriteGate gate)
    {
        _db = db;
        _gate = gate;
    }

    /// <summary>Canonical defaults (docs/05 §1.7). Values are strings (scalar or JSON).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["max_global_concurrent_recordings"] = "4",
        ["default_pre_pad_s"] = "300",
        ["default_post_pad_s"] = "1800",
        // TheSportsDB never gives an end time, so DVarr assumes one. default_event_duration_s is the base (2h —
        // a soccer match + stoppage fits in 2h core + the 30-min post-pad). event_duration_overrides_json maps a
        // lowercased sport name to its own seconds (e.g. "motorsport":10800 keeps a long window instead of being cut
        // at 2h). NOTE: for MOTORSPORT this per-sport value is only a FALLBACK — GetEventDurationSecondsAsync resolves
        // an earlier BUILT-IN per-session default first (MotorsportSession.DefaultDurationS: a Sprint/Quali/Practice ~1h,
        // Race/Testing 3h), so this "motorsport" entry applies only to a session kind the built-ins don't cover.
        // Edit the JSON to add sports (e.g. add "american football": 12600).
        ["default_event_duration_s"] = "7200",
        // Sport-audit defaults (2026-07-04): sports whose events routinely exceed the 2h base get their own assumed
        // length — golf rounds ~6h, fight cards (UFC etc.) ~5h, tennis/cricket ~4h (T20-ish; long formats use the
        // per-league override). Keys = lowercased TheSportsDB strSport; matching is case-insensitive. Auto-stop
        // extends past these when the event genuinely runs long.
        ["event_duration_overrides_json"] = "{\"motorsport\":10800,\"fighting\":18000,\"golf\":21600,\"cricket\":14400,\"tennis\":14400}",
        // Bitrate-floor placeholder detection (opt-in, like dead-feed detection): when bitrate_floor_enabled is on, a
        // recording whose rolling stream bitrate stays below the floor for its channel's quality tier for a sustained
        // window is treated as a provider placeholder/slate (bytes flow, but far too few for real content) and fails
        // over down the same ladder as a stall/dead-picture. Floors are per tier — SD, HD (720p/1080p), UHD (4K/2160p);
        // an unclassified channel uses the SD floor (the most permissive) so real streams aren't wrongly dropped.
        ["bitrate_floor_enabled"] = "false",
        ["bitrate_floor_kbps_sd"] = "400",
        ["bitrate_floor_kbps_hd"] = "800",
        ["bitrate_floor_kbps_uhd"] = "2000",
        ["segment_no_progress_timeout_s"] = "25",
        // Content verification (docs/04 §dead-feed). When ON, a second decode-only output of the SAME ffmpeg
        // connection (no extra login) watches for a black/frozen/silent slate; sustained dead picture for
        // content_dead_timeout_s routes into the normal relaunch→failover ladder. OFF by default — opt-in after soak.
        ["content_verify_enabled"] = "false",
        ["content_dead_timeout_s"] = "30",
        // The dead-feed decode runs on the GPU (NVDEC) and samples only a few frames/sec, so it costs almost no CPU
        // (software-decoding both live feeds was the single biggest CPU draw). content_verify_hwaccel = the ffmpeg
        // -hwaccel method for that decode ("cuda" = NVDEC on the Nvidia GPU; "" or "none" = software/CPU decode).
        // content_verify_fps caps how many frames/sec the black/freeze filters sample (1 is ample for a dead-slate
        // check; 0 = every frame). Both apply only when content_verify_enabled is on; the recording itself stays -c copy.
        ["content_verify_hwaccel"] = "cuda",
        ["content_verify_fps"] = "1",
        // Reliability (docs/04). De-overlap trims duplicate seconds the provider re-serves on reconnect so the
        // finished file never "goes back in time"; clean-EOF instant relaunch rides momentary line drops without
        // Recovering churn. Both ON by default; flip to false to fall back to plain concat / treat clean EOF as a fault.
        ["finalize_deoverlap_enabled"] = "true",
        ["clean_eof_instant_relaunch"] = "true",
        ["tick_interval_s"] = "10",
        ["auto_schedule_interval_s"] = "300",
        ["event_sync_interval_s"] = "21600",
        // EPG retention window + safety cap (the full external EPG is stored within this window).
        ["epg_past_window_h"] = "48",
        ["epg_future_window_d"] = "21",
        ["epg_max_programmes"] = "3000000",
        // Optional automatic EPG refresh: when ON, DVarr syncs every enabled source's guide once a day at
        // epg_auto_sync_time (HH:MM) in the FIXED offset epg_auto_sync_offset_minutes (minutes east of UTC; 600 =
        // Brisbane +10). The container ships no tz database, so this is a pure offset (no daylight-saving shift) — exact
        // for Brisbane. OFF by default; the manual per-source EPG button is unaffected. (epg_auto_sync_time's default
        // must stay non-numeric so the /api/settings int-guard treats it as text.)
        ["epg_auto_sync_enabled"] = "false",
        ["epg_auto_sync_time"] = "04:00",
        ["epg_auto_sync_offset_minutes"] = "600",
        // Internal bookkeeping (NOT a user setting; hidden by the UI): the local date ("yyyy-MM-dd") the daily EPG sync
        // last fired — persisted so a container redeploy after the fire time doesn't re-run the whole sync. Must live in
        // Defaults or EnsureDefaultsAsync's orphan-prune would delete the row on every boot.
        ["epg_auto_sync_last"] = "",
        // TheSportsDB v2 API key (premium / Patreon), sent as the X-API-KEY header. Empty by default = use the key
        // BUNDLED with the build (baked into the official image; see TheSportsDbClient). Paste your own key here to
        // override it. The bundled key itself never enters this table, so GET /api/settings can never expose it.
        // NOTE: a text setting whose default must NOT look numeric, or the /api/settings int-guard would reject a
        // numeric key that overflows Int32 (premium keys are 10 digits).
        ["thesportsdb_api_key"] = "",
        // Arm-window EPG re-pick: when ON, a Pending recording within ~24h of start is re-resolved against the live
        // guide and moved (same credential only) to whichever mapped channel's EPG actually shows the event.
        ["epg_repick_enabled"] = "true",
        // When a recording's pre-roll attempt captures nothing (e.g. the channel isn't live yet), make ONE guaranteed
        // fresh attempt at the event's real start time. Never interrupts a recording that's already capturing.
        ["retry_at_event_start"] = "true",
        // Smart auto-stop (Phase 21) kill-switch: when ON, AutoStopService polls TheSportsDB near a live recording's
        // scheduled end and EXTENDS the window while the event is still in play (extra time / penalties), closing it
        // once the guide reports a terminal status. Never shortens below the scheduled window. OFF = fixed windows only.
        ["auto_stop_enabled"] = "true",
        ["timezone_display"] = "Australia/Brisbane",
        ["ha_webhook_url"] = "",
        // Public base URL of this DVarr instance (e.g. https://dvr.example.com), used to build the externally-reachable
        // calendar-feed link shown in the "Subscribe" modal. Empty by default — the UI prompts for it there. A text
        // setting whose default must NOT look numeric, or the /api/settings int-guard would reject a real URL.
        ["public_base_url"] = "",
    };

    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        var existing = await _db.Settings.Select(s => s.Key).ToListAsync(ct);
        var missing = Defaults.Where(kv => !existing.Contains(kv.Key)).ToList();
        // Prune setting rows whose key has been REMOVED from Defaults (e.g. the retired threadfin_base_url) so a stale
        // row can't surface in the UI's "Advanced" group or trip the allowlisted PUT /api/settings.
        var orphan = existing.Where(k => !Defaults.ContainsKey(k)).ToList();
        if (missing.Count == 0 && orphan.Count == 0) return;

        await _gate.WriteAsync(async () =>
        {
            if (orphan.Count > 0) await _db.Settings.Where(s => orphan.Contains(s.Key)).ExecuteDeleteAsync(ct);
            foreach (var kv in missing)
                _db.Settings.Add(new Setting { Key = kv.Key, Value = kv.Value, UpdatedUtc = EpochTime.Now() });
            if (missing.Count > 0) await _db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task<string?> GetAsync(string key)
    {
        var row = await _db.Settings.FindAsync(key);
        if (row != null) return row.Value;
        return Defaults.TryGetValue(key, out var d) ? d : null;
    }

    public async Task<int> GetIntAsync(string key)
        => int.TryParse(await GetAsync(key), out var n) ? n : 0;

    /// <summary>
    /// Seconds to assume an event runs when the provider gives no end time. Resolution order (most specific first):
    /// (0) the league's USER-set per-SESSION map <paramref name="sessionDurationsJson"/> for this event's session kind;
    /// (1) the per-LEAGUE override <paramref name="leagueOverrideS"/> if &gt; 0; (2) the BUILT-IN motorsport per-session
    /// default (MotorsportSession.DefaultDurationS — motorsport leagues only) so an F1 Sprint/Quali/Practice assumes ~1h
    /// instead of inheriting the 3h motorsport sport default; (3) the per-SPORT override (event_duration_overrides_json,
    /// keyed by lowercased sport); (4) default_event_duration_s (2h fallback). The built-in sits BELOW the league-wide
    /// override (an explicit user override always wins, for predictability) but ABOVE the generic sport default.
    /// Soccer → 2h core (+post-pad); a motorsport race → 3h so race endings aren't clipped, a support session → ~1h.
    /// </summary>
    public async Task<int> GetEventDurationSecondsAsync(string? sport, int? leagueOverrideS = null, string? sessionDurationsJson = null, string? eventTitle = null)
    {
        // A motorsport session kind, classified once from the title (null for non-motorsport / empty title) — reused by
        // tiers 0 and 2 below so both consult the same classification.
        var kind = Events.MotorsportSession.IsMotorsport(sport) && !string.IsNullOrWhiteSpace(eventTitle)
            ? Events.MotorsportSession.Classify(eventTitle) : null;
        // Tier 0 (most specific): the league's USER-set per-SESSION map for this event's session kind (e.g. a 3h race vs
        // a 1h practice). Only applies when the league set session durations, and ONLY for motorsport — any other sport's
        // titles all classify as "Race", which would misapply one length everywhere.
        if (kind != null && !string.IsNullOrWhiteSpace(sessionDurationsJson))
        {
            var map = ParseSessionDurations(sessionDurationsJson);
            if (map.Count > 0 && map.TryGetValue(kind, out var ss)) return ss;
        }
        if (leagueOverrideS is > 0) return leagueOverrideS.Value; // tier 1: explicit per-league override wins
        // Tier 2: built-in motorsport per-session default (Sprint/Quali/Practice ~1h, Race/Testing 3h). Sits below the
        // league override but above the generic sport default so a support session stops booking a full 3h window.
        if (kind != null && Events.MotorsportSession.DefaultDurationS(kind) is { } builtin) return builtin;
        var def = await GetIntAsync("default_event_duration_s"); if (def <= 0) def = 7200;
        if (string.IsNullOrWhiteSpace(sport)) return def;
        var json = await GetAsync("event_duration_overrides_json");
        if (string.IsNullOrWhiteSpace(json)) return def;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return def;
            var key = sport!.Trim();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name.Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n) && n > 0) return n;
                if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var s) && s > 0) return s;
            }
        }
        catch { /* malformed override JSON → use the base default */ }
        return def;
    }

    /// <summary>Parse a League.SessionDurationsJson map (session-kind → SECONDS). Tolerates number or string values;
    /// drops non-positive entries. Empty/malformed → no overrides.</summary>
    public static Dictionary<string, int> ParseSessionDurations(string? json)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var v = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n) ? n
                          : p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var s) ? s : 0;
                    if (v > 0 && !string.IsNullOrWhiteSpace(p.Name)) map[p.Name.Trim()] = v;
                }
        }
        catch { /* malformed → no per-session overrides */ }
        return map;
    }

    public async Task<bool> GetBoolAsync(string key)
    {
        var v = (await GetAsync(key))?.Trim();
        return v is "true" or "1" or "yes" or "on";
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
        => await SetManyAsync(new Dictionary<string, string> { [key] = value }, ct);

    /// <summary>Persist a batch of settings in ONE write-gate transaction (single SaveChanges), so a DB error,
    /// cancellation, or shutdown mid-save can never leave a half-applied Settings page (audit SET-03).</summary>
    public async Task SetManyAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        if (values.Count == 0) return;
        await _gate.WriteAsync(async () =>
        {
            var now = EpochTime.Now();
            foreach (var kv in values)
            {
                var row = await _db.Settings.FindAsync(kv.Key);
                if (row == null)
                {
                    row = new Setting { Key = kv.Key };
                    _db.Settings.Add(row);
                }
                row.Value = kv.Value;
                row.UpdatedUtc = now;
            }
            await _db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync()
    {
        var rows = await _db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value);
        // Overlay any unset defaults so callers always see a complete view.
        foreach (var kv in Defaults)
            rows.TryAdd(kv.Key, kv.Value);
        return rows;
    }
}
