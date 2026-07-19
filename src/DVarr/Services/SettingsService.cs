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

    /// <summary>A per-sport recording profile: assumed event length, smart-auto-stop extension cap, and pre/post-roll
    /// padding — all in SECONDS, all optional (null = inherit the global default). Keyed by lowercased TheSportsDB
    /// strSport in <c>sport_defaults_json</c>.</summary>
    public sealed record SportProfile(int? Len, int? Cap, int? Pre, int? Post);

    /// <summary>Seed for <c>sport_defaults_json</c> — sensible defaults for the common sports (seconds), editable in
    /// Settings → Recording profiles. Baseball's 3h cap is the rain-delay headroom that fix motivated.</summary>
    public static readonly string DefaultSportProfilesJson = JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["american football"] = new { len = 12600, cap = 5400, pre = 300, post = 1800 },
        ["australian football"] = new { len = 8400, cap = 3600, pre = 300, post = 1800 },
        ["baseball"] = new { len = 10800, cap = 10800, pre = 300, post = 1800 },
        ["basketball"] = new { len = 8100, cap = 3600, pre = 300, post = 1800 },
        ["cricket"] = new { len = 14400, cap = 10800, pre = 300, post = 1800 },
        ["cycling"] = new { len = 18000, cap = 3600, pre = 300, post = 900 },
        ["darts"] = new { len = 10800, cap = 5400, pre = 300, post = 900 },
        ["fighting"] = new { len = 18000, cap = 7200, pre = 600, post = 3600 },
        ["golf"] = new { len = 21600, cap = 7200, pre = 300, post = 1800 },
        ["ice hockey"] = new { len = 9000, cap = 5400, pre = 300, post = 1800 },
        ["motorsport"] = new { len = 10800, cap = 7200, pre = 600, post = 1800 },
        ["rugby"] = new { len = 7200, cap = 3600, pre = 300, post = 1800 },
        ["snooker"] = new { len = 14400, cap = 10800, pre = 300, post = 900 },
        ["soccer"] = new { len = 7200, cap = 3600, pre = 300, post = 1800 },
        ["tennis"] = new { len = 10800, cap = 10800, pre = 300, post = 1800 },
    });

    /// <summary>Canonical defaults (docs/05 §1.7). Values are strings (scalar or JSON).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["max_global_concurrent_recordings"] = "4",
        ["default_pre_pad_s"] = "300",
        ["default_post_pad_s"] = "1800",
        // TheSportsDB never gives an end time, so DVarr assumes one. default_event_duration_s is the GLOBAL base (2h)
        // used for any sport without its own profile. Per-sport assumed length + auto-stop cap + pre/post padding live
        // in sport_defaults_json (Settings → Recording profiles), seeded below; a per-league override still wins.
        ["default_event_duration_s"] = "7200",
        // Global smart-auto-stop extension cap (seconds) for any sport without its own profile cap. Per-sport caps sit
        // in sport_defaults_json (e.g. baseball 3h for rain delays); a per-league override still wins.
        ["default_auto_stop_cap_s"] = "3600",
        // Per-sport recording profiles: {sport:{len,cap,pre,post}} in seconds; any omitted field inherits the matching
        // global default. Seeded with sensible defaults for the common sports — edit in Settings → Recording profiles.
        ["sport_defaults_json"] = DefaultSportProfilesJson,
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
        ["epg_auto_sync_enabled"] = "true",
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
        // National-broadcast fallback: when a monitored game airs on a channel you DIDN'T map (e.g. it moved to ESPN)
        // and no mapped channel's guide shows it, search the whole provider for a channel whose guide clearly shows the
        // matchup (BOTH teams) and record there instead. Same login only; requires the guide to actually list the game.
        ["national_fallback_enabled"] = "true",
        // When a recording's pre-roll attempt captures nothing (e.g. the channel isn't live yet), make ONE guaranteed
        // fresh attempt at the event's real start time. Never interrupts a recording that's already capturing.
        ["retry_at_event_start"] = "true",
        // Smart auto-stop (Phase 21) kill-switch: when ON, AutoStopService polls TheSportsDB near a live recording's
        // scheduled end and EXTENDS the window while the event is still in play (extra time / penalties), closing it
        // once the guide reports a terminal status. Never shortens below the scheduled window. OFF = fixed windows only.
        ["auto_stop_enabled"] = "true",
        // Chapter markers: while a recording is live, AutoStopService also records the match's status transitions
        // (kick-off, half-time, extra time, penalties, full time) and finalize embeds them as MKV chapters that
        // Plex/Jellyfin/Kodi/VLC read natively. Independent of auto_stop_enabled (a "fixed" league still gets
        // chapters); OFF = no status polling for chapter purposes.
        ["chapter_marks_enabled"] = "true",
        // Second-chance replay rescue: when a monitored-league game ends with no playable copy (failed finalize,
        // missed window, or an unresolved conflict), open a rescue ticket and let a background sweep hunt the guide
        // for a re-air, scheduling it as a low-priority replay that never preempts a live game. whole_source widens
        // the search from the league's mapped channels to every channel on those sources; give_up_days abandons the
        // hunt after N days; interval_s is how often the sweep runs.
        ["replay_rescue_enabled"] = "true",
        ["replay_rescue_whole_source"] = "false",
        ["replay_rescue_give_up_days"] = "3",
        ["replay_rescue_interval_s"] = "900",
        // Disk-space guardrails: warn (never block) when a filesystem is under its free floor, or when a new recording
        // is projected to push it under. GB; 0 = that floor disabled. Media = the final library volume; segments = the
        // in-flight capture scratch (may be a different filesystem — the guardrail checks both).
        ["disk_min_free_gb"] = "10",
        ["disk_min_free_segments_gb"] = "10",
        // Retention: automatically evict old finished recordings per policy. Default keep_all = nothing is ever deleted
        // until you choose a policy (globally here, or per-league on the Leagues page). Modes: keep_all | keep_last_n |
        // keep_days | gb_cap | watched (needs the Plex/Jellyfin watched webhook). A per-recording Protect flag always
        // wins, eviction is unprotected-oldest-first, and the Storage settings tab has a dry-run preview.
        // (retention_default_mode's default must stay non-numeric so the /api/settings int-guard treats it as text.)
        ["retention_default_mode"] = "keep_all",
        ["retention_keep_last"] = "20",
        ["retention_keep_days"] = "90",
        ["retention_gb_cap"] = "500",
        // "Delete after watched": ON = remove a game the instant your media server reports it watched; OFF = just flag
        // it and let the daily cleanup delete it. The daily cleanup runs once per day at retention_sweep_time (LOCAL to
        // your Display timezone) and applies every league's policy. retention_sweep_last is an internal fire stamp
        // (the last local date it ran — must stay in Defaults or EnsureDefaults would prune the row each boot).
        ["retention_watched_instant"] = "true",
        ["retention_sweep_time"] = "03:00",
        ["retention_sweep_last"] = "",
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
        // Tier 3: the sport's profile length (sport_defaults_json), else the global default.
        var profiles = ParseSportProfiles(await GetAsync("sport_defaults_json"));
        return profiles.TryGetValue(sport!.Trim(), out var prof) && prof.Len is > 0 ? prof.Len.Value : def;
    }

    /// <summary>Parse sport_defaults_json — {sport:{len,cap,pre,post}} (seconds). Keyed case-insensitively; a field
    /// &lt;= 0 or absent means "inherit the global default" (null).</summary>
    public static Dictionary<string, SportProfile> ParseSportProfiles(string? json)
    {
        var map = new Dictionary<string, SportProfile>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(p.Name) || p.Value.ValueKind != JsonValueKind.Object) continue;
                map[p.Name.Trim()] = new SportProfile(SportField(p.Value, "len"), SportField(p.Value, "cap"), SportField(p.Value, "pre"), SportField(p.Value, "post"));
            }
        }
        catch { /* malformed → no profiles */ }
        return map;
    }

    private static int? SportField(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) && s > 0) return s;
        return null;
    }

    /// <summary>Serialize sport profiles to the compact {sport:{len,cap,pre,post}} form, omitting null/&lt;=0 fields
    /// and empty sports.</summary>
    public static string SerializeSportProfiles(IReadOnlyDictionary<string, SportProfile> profiles)
    {
        var o = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (sport, p) in profiles)
        {
            var d = new Dictionary<string, int>();
            if (p.Len is > 0) d["len"] = p.Len.Value;
            if (p.Cap is > 0) d["cap"] = p.Cap.Value;
            if (p.Pre is > 0) d["pre"] = p.Pre.Value;
            if (p.Post is > 0) d["post"] = p.Post.Value;
            if (d.Count > 0 && !string.IsNullOrWhiteSpace(sport)) o[sport.Trim()] = d;
        }
        return JsonSerializer.Serialize(o);
    }

    /// <summary>Smart-auto-stop extension cap (seconds): per-league override → the sport's profile cap → the global
    /// default_auto_stop_cap_s. Baseball's profile carries a 3h cap so rain delays aren't clipped.</summary>
    public async Task<int> GetAutoStopCapSecondsAsync(string? sport, int? leagueOverrideS = null)
    {
        if (leagueOverrideS is > 0) return leagueOverrideS.Value;
        if (!string.IsNullOrWhiteSpace(sport))
        {
            var profiles = ParseSportProfiles(await GetAsync("sport_defaults_json"));
            if (profiles.TryGetValue(sport!.Trim(), out var prof) && prof.Cap is > 0) return prof.Cap.Value;
        }
        var def = await GetIntAsync("default_auto_stop_cap_s");
        return def > 0 ? def : 3600;
    }

    /// <summary>Pre/post-roll padding (seconds) for a sport: the sport's profile pads, else the global
    /// default_pre_pad_s / default_post_pad_s (an explicit global 0 is respected).</summary>
    public async Task<(int Pre, int Post)> GetPadsForSportAsync(string? sport)
    {
        var pre = int.TryParse(await GetAsync("default_pre_pad_s"), out var pr) && pr >= 0 ? pr : 300;
        var post = int.TryParse(await GetAsync("default_post_pad_s"), out var po) && po >= 0 ? po : 1800;
        if (!string.IsNullOrWhiteSpace(sport))
        {
            var profiles = ParseSportProfiles(await GetAsync("sport_defaults_json"));
            if (profiles.TryGetValue(sport!.Trim(), out var prof))
            {
                if (prof.Pre is > 0) pre = prof.Pre.Value;
                if (prof.Post is > 0) post = prof.Post.Value;
            }
        }
        return (pre, post);
    }

    /// <summary>One-time migration (v1.41.4): fold the retired flat per-sport length map (event_duration_overrides_json)
    /// into the richer sport_defaults_json profiles, preserving any user-customised lengths, then drop the old key.
    /// Runs BEFORE EnsureDefaultsAsync so the old key isn't pruned before it's read. Defensive — never throws.</summary>
    public async Task MigrateSportProfilesAsync(CancellationToken ct = default)
    {
        try
        {
            var oldRow = await _db.Settings.FirstOrDefaultAsync(s => s.Key == "event_duration_overrides_json", ct);
            if (oldRow is null) return; // already migrated / fresh install

            var current = await _db.Settings.FirstOrDefaultAsync(s => s.Key == "sport_defaults_json", ct);
            var profiles = ParseSportProfiles(current?.Value ?? DefaultSportProfilesJson);
            try
            {
                using var doc = JsonDocument.Parse(oldRow.Value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        int? oldLen = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n) && n > 0 ? n
                                    : p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var s) && s > 0 ? s : null;
                        if (oldLen is null || string.IsNullOrWhiteSpace(p.Name)) continue;
                        var key = p.Name.Trim();
                        var ex = profiles.TryGetValue(key, out var pr) ? pr : new SportProfile(null, null, null, null);
                        profiles[key] = ex with { Len = oldLen };
                    }
            }
            catch { /* malformed old JSON → keep the seed profiles, just drop the old key */ }

            var merged = SerializeSportProfiles(profiles);
            await _gate.WriteAsync(async () =>
            {
                var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == "sport_defaults_json", ct);
                if (row is null) _db.Settings.Add(new Setting { Key = "sport_defaults_json", Value = merged, UpdatedUtc = EpochTime.Now() });
                else { row.Value = merged; row.UpdatedUtc = EpochTime.Now(); }
                _db.Settings.Remove(oldRow);
                await _db.SaveChangesAsync(ct);
            }, ct);
        }
        catch { /* migration is best-effort; a failure just leaves the old key for next boot */ }
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
