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
        // lowercased sport name to its own seconds so motorsport keeps a long window ("keep full race endings"
        // rule) instead of being cut at 2h. Edit the JSON to add sports (e.g. add "american football": 12600).
        ["default_event_duration_s"] = "7200",
        ["event_duration_overrides_json"] = "{\"motorsport\":10800}",
        ["bitrate_floor_kbps_sd"] = "400",
        ["bitrate_floor_kbps_hd"] = "800",
        ["segment_no_progress_timeout_s"] = "25",
        ["content_probe_interval_s"] = "45",
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
        // TheSportsDB v2 API key (premium / Patreon), sent as the X-API-KEY header. Empty by default — v2 needs a real
        // (paid) key; paste yours in Settings to unlock the full catalogue (AFL, NRL, all sports) + complete seasons.
        // NOTE: a text setting whose default must NOT look numeric, or the /api/settings int-guard would reject a
        // numeric key that overflows Int32 (premium keys are 10 digits).
        ["thesportsdb_api_key"] = "",
        ["recorder_input_mode"] = "direct_ts",
        // When a recording's pre-roll attempt captures nothing (e.g. the channel isn't live yet), make ONE guaranteed
        // fresh attempt at the event's real start time. Never interrupts a recording that's already capturing.
        ["retry_at_event_start"] = "true",
        ["default_channel_source_filter"] = "all",
        ["timezone_display"] = "Australia/Brisbane",
        ["ha_webhook_url"] = "",
        ["litestream_target"] = "",
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
    /// Seconds to assume an event runs when the provider gives no end time. Resolution order:
    /// (1) the per-LEAGUE override <paramref name="leagueOverrideS"/> if &gt; 0, then (2) the per-SPORT override
    /// (event_duration_overrides_json, keyed by lowercased sport), then (3) default_event_duration_s (2h fallback).
    /// Soccer → 2h core (+post-pad); motorsport → 3h so race endings aren't clipped.
    /// </summary>
    public async Task<int> GetEventDurationSecondsAsync(string? sport, int? leagueOverrideS = null, string? sessionDurationsJson = null, string? eventTitle = null)
    {
        // Tier 0 (most specific): a motorsport per-SESSION override for this event's session kind (e.g. a 3h race vs a 1h
        // practice). Classified from the title via MotorsportSession; only applies when the league set session durations,
        // and ONLY for motorsport — any other sport's titles all classify as "Race", which would misapply one length everywhere.
        if (Events.MotorsportSession.IsMotorsport(sport) && !string.IsNullOrWhiteSpace(sessionDurationsJson) && !string.IsNullOrWhiteSpace(eventTitle))
        {
            var map = ParseSessionDurations(sessionDurationsJson);
            if (map.Count > 0 && Events.MotorsportSession.Classify(eventTitle) is { } kind && map.TryGetValue(kind, out var ss)) return ss;
        }
        if (leagueOverrideS is > 0) return leagueOverrideS.Value; // tier 1: explicit per-league override wins
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
    {
        await _gate.WriteAsync(async () =>
        {
            var row = await _db.Settings.FindAsync(key);
            if (row == null)
            {
                row = new Setting { Key = key };
                _db.Settings.Add(row);
            }
            row.Value = value;
            row.UpdatedUtc = EpochTime.Now();
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
