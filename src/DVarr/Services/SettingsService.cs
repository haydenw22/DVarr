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
        // TheSportsDB API key. "3" is the public TEST key (sample data: Soccer + Motorsport only). Paste your own
        // key here to unlock the full sports/leagues catalogue (AFL, all F1/Supercars, etc.).
        ["thesportsdb_api_key"] = "3",
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
    public async Task<int> GetEventDurationSecondsAsync(string? sport, int? leagueOverrideS = null)
    {
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
