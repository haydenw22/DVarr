using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Media;

public sealed record RetentionVictim(int Id, string Title, string Show, long StartUtc, long Bytes, bool Watched);
public sealed record LeagueRetentionPlan(int? LeagueId, string League, string Mode, string Detail, List<RetentionVictim> Victims, long BytesFreed);

/// <summary>
/// Retention: evict old finished recordings per a per-league (or global-default) policy — keep-last-N, keep-N-days,
/// a GB cap, or delete-after-watched. Eviction is always unprotected-oldest-first and NEVER touches a Protected
/// item. <see cref="PlanAsync"/> computes the plan without deleting (the dry-run preview); <see cref="SweepAsync"/>
/// carries it out, reusing the same safe delete-with-files primitive the manual Library delete uses.
/// </summary>
public sealed class RetentionService
{
    private readonly DVarrDbContext _db;
    private readonly SettingsService _settings;
    private readonly LibraryService _lib;
    private readonly DbWriteGate _gate;
    private readonly LibraryPlaybackManager _playback;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(DVarrDbContext db, SettingsService settings, LibraryService lib, DbWriteGate gate,
        LibraryPlaybackManager playback, ILogger<RetentionService> log)
    { _db = db; _settings = settings; _lib = lib; _gate = gate; _playback = playback; _log = log; }

    /// <summary>Compute the eviction plan per league WITHOUT deleting anything (drives the dry-run preview and the
    /// sweep). Only leagues with an effective non-"keep_all" policy and at least one eviction candidate appear.</summary>
    public async Task<List<LeagueRetentionPlan>> PlanAsync(CancellationToken ct = default)
    {
        var globalMode = (await _settings.GetAsync("retention_default_mode") ?? "keep_all").Trim();
        var gLast = await _settings.GetIntAsync("retention_keep_last");
        var gDays = await _settings.GetIntAsync("retention_keep_days");
        var gGb = await _settings.GetIntAsync("retention_gb_cap");
        var watchedCfg = await LoadWatchedConfigAsync();
        var now = EpochTime.Now();

        var leagues = await _db.Leagues.AsNoTracking()
            .Select(l => new { l.Id, l.Name, l.RetentionMode, l.RetentionKeepLast, l.RetentionKeepDays, l.RetentionGbCap })
            .ToListAsync(ct);

        var plans = new List<LeagueRetentionPlan>();
        foreach (var l in leagues)
        {
            var mode = string.IsNullOrWhiteSpace(l.RetentionMode) ? globalMode : l.RetentionMode!.Trim();
            if (mode == "keep_all") continue;
            var keepLast = l.RetentionKeepLast ?? gLast;
            var keepDays = l.RetentionKeepDays ?? gDays;
            var gbCap = l.RetentionGbCap ?? gGb;

            // Candidate items: this league's OK, filed (not unsorted), UNPROTECTED files, newest first. Protected
            // items are excluded BEFORE the policy runs (audit RET-01): they must not consume a keep-newest-N slot
            // or GB-cap budget — "keep the newest 1" with the newest protected keeps the protected one AND the
            // newest unprotected one, it doesn't sacrifice an extra unprotected recording.
            var items = await _db.LibraryItems.AsNoTracking()
                .Where(i => i.LeagueId == l.Id && i.Status == LibraryItemStatus.Ok && !i.Unsorted && !i.Protected)
                .OrderByDescending(i => i.StartUtc).ThenByDescending(i => i.Id)
                .ToListAsync(ct);
            if (items.Count == 0) continue;

            var (victims, detail) = SelectVictims(mode, items, keepLast, keepDays, gbCap, now, watchedCfg);
            plans.Add(new LeagueRetentionPlan(l.Id, l.Name, mode, detail,
                victims.Select(i => new RetentionVictim(i.Id, i.Title, i.ShowName, i.StartUtc, i.FileBytes, i.WatchedUtc != null)).ToList(),
                victims.Sum(i => i.FileBytes)));
        }
        return plans;
    }

    /// <summary>Carry out the plan: delete each victim's files (safe folder-pruning primitive) and remove its row,
    /// stopping any active playback first. When <paramref name="confirmedIds"/> is supplied (the UI's "Delete these
    /// now" after a preview — audit RET-02), execution is bound to that reviewed list: if a fresh plan selects ANY
    /// item the user didn't see (a new game landed, protection/policy changed), nothing is deleted and
    /// <c>PlanChanged</c> is true so the UI re-previews. Confirmed items no longer in the plan are simply skipped.</summary>
    public async Task<(int Deleted, long BytesFreed, bool PlanChanged)> SweepAsync(
        IReadOnlyCollection<int>? confirmedIds = null, CancellationToken ct = default)
    {
        var plans = await PlanAsync(ct);
        var victimIds = plans.SelectMany(p => p.Victims.Select(v => v.Id)).Distinct().ToList();
        if (confirmedIds is not null)
        {
            var confirmed = confirmedIds.ToHashSet();
            if (victimIds.Any(id => !confirmed.Contains(id))) return (0, 0, true); // never delete what wasn't reviewed
        }
        if (victimIds.Count == 0) return (0, 0, false);

        int deleted = 0; long freed = 0;
        foreach (var id in victimIds)
        {
            ct.ThrowIfCancellationRequested();
            var item = await _db.LibraryItems.FindAsync(new object?[] { id }, ct);
            if (item is null || item.Status != LibraryItemStatus.Ok || item.Protected) continue; // re-check under a fresh read
            await _playback.StopAsync(id); // never delete a file out from under its own playback session
            var err = _lib.DeleteItemFiles(item);
            if (err is not null) { _log.LogWarning("[Retention] file delete failed for {Id}: {Err}", id, err); continue; }
            var bytes = item.FileBytes;
            await _gate.WriteAsync(async () => { _db.LibraryItems.Remove(item); await _db.SaveChangesAsync(ct); }, ct);
            deleted++; freed += bytes;
        }
        if (deleted > 0)
        {
            var now = EpochTime.Now();
            await _gate.WriteAsync(async () =>
            {
                _db.Notifications.Add(new Notification
                {
                    TsUtc = now, Kind = NotificationKind.RetentionEvicted, Severity = Severity.Info,
                    Message = $"retention removed {deleted} old recording(s), freeing {freed / 1_000_000_000.0:0.0} GB",
                });
                await _db.SaveChangesAsync(ct);
            }, ct);
            _log.LogInformation("[Retention] evicted {N} item(s), freed {Gb:0.0} GB", deleted, freed / 1_000_000_000.0);
        }
        return (deleted, freed, false);
    }

    /// <summary>The "delete after watched" safety window: threshold the media server flags a game watched at (a
    /// POSITION %, not true end-of-play), the buffer added past the estimated finish, and a flat fallback delay used
    /// only when a file's runtime is unknown so the estimate can't be computed.</summary>
    private readonly record struct WatchedRetentionConfig(int ThresholdPct, int BufferMinutes, int FallbackDelayMinutes);

    private async Task<WatchedRetentionConfig> LoadWatchedConfigAsync()
    {
        var threshold = await _settings.GetIntAsync("retention_watched_threshold_pct");
        if (threshold is <= 0 or >= 100) threshold = 90;                 // must leave a non-empty tail
        var buffer = await _settings.GetIntAsync("retention_watched_buffer_minutes");
        if (buffer < 0) buffer = 15;
        var delay = await _settings.GetIntAsync("retention_watched_delay_minutes");
        if (delay < 0) delay = 60;
        return new WatchedRetentionConfig(threshold, buffer, delay);
    }

    /// <summary>Epoch before which a just-watched game must NOT be deleted. The media server flags "watched" at a
    /// position threshold (~90%), so the un-watched tail ≈ DurationS × (100 − threshold%); the earliest safe delete
    /// is WatchedUtc + that tail + the buffer. When the runtime is unknown (probe failed) the tail can't be sized, so
    /// a flat fallback delay applies instead.</summary>
    private static long WatchedDeleteEarliest(long watchedUtc, int? durationS, WatchedRetentionConfig cfg)
    {
        long tailS = durationS is int d && d > 0
            ? (long)Math.Round(d * (100 - cfg.ThresholdPct) / 100.0) + cfg.BufferMinutes * 60L
            : cfg.FallbackDelayMinutes * 60L;
        return watchedUtc + tailS;
    }

    /// <summary>Delete "delete after watched" games whose safety window has elapsed — the estimated end-of-play plus
    /// the buffer (see <see cref="WatchedDeleteEarliest"/>). Called every schedule tick so a finished game is cleaned
    /// up within minutes, but NEVER before the viewer would have reached the end (the media server flags watched at a
    /// ~90% position, not at true end-of-play — deleting on that signal wiped files mid-watch). Items under any other
    /// policy are left to the scheduled sweep. Returns how many were removed.</summary>
    public async Task<int> EvictWatchedDueAsync(CancellationToken ct = default)
    {
        var cfg = await LoadWatchedConfigAsync();
        var globalMode = (await _settings.GetAsync("retention_default_mode") ?? "keep_all").Trim();
        var now = EpochTime.Now();

        // Cheap filtered read — watched, filed, unprotected candidates; the per-item league-mode + window checks run
        // in memory. Protected/unsorted are excluded here AND re-checked under a fresh read before each delete.
        var candidates = await _db.LibraryItems.AsNoTracking()
            .Where(i => i.Status == LibraryItemStatus.Ok && !i.Protected && !i.Unsorted && i.WatchedUtc != null)
            .Select(i => new { i.Id, i.LeagueId, i.WatchedUtc, i.DurationS })
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        // Effective mode = the item's league policy, else the global default. Keep only "watched" items whose safety
        // window has passed.
        var leagueModes = await _db.Leagues.AsNoTracking()
            .Where(l => l.RetentionMode != null && l.RetentionMode != "")
            .ToDictionaryAsync(l => l.Id, l => l.RetentionMode!.Trim(), ct);
        var dueIds = new List<int>();
        foreach (var c in candidates)
        {
            var mode = c.LeagueId is int lid && leagueModes.TryGetValue(lid, out var lm) ? lm : globalMode;
            if (mode != "watched") continue;
            if (now >= WatchedDeleteEarliest(c.WatchedUtc!.Value, c.DurationS, cfg)) dueIds.Add(c.Id);
        }
        if (dueIds.Count == 0) return 0;

        int deleted = 0;
        foreach (var id in dueIds)
        {
            ct.ThrowIfCancellationRequested();
            var item = await _db.LibraryItems.FindAsync(new object?[] { id }, ct);
            if (item is null || item.Status != LibraryItemStatus.Ok || item.Protected || item.Unsorted) continue; // re-check under a fresh read
            await _playback.StopAsync(id); // never delete a file out from under its own playback session
            var err = _lib.DeleteItemFiles(item);
            if (err is not null) { _log.LogWarning("[Retention] delete-after-watched failed for {Id}: {Err}", id, err); continue; }
            var (title, bytes, recId) = (item.Title, item.FileBytes, item.RecordingId);
            await _gate.WriteAsync(async () =>
            {
                _db.LibraryItems.Remove(item);
                _db.Notifications.Add(new Notification
                {
                    RecordingId = recId, TsUtc = now, Kind = NotificationKind.RetentionEvicted, Severity = Severity.Info,
                    Message = $"deleted “{title}” after you finished watching it — {bytes / 1_000_000_000.0:0.0} GB freed",
                });
                await _db.SaveChangesAsync(ct);
            }, ct);
            deleted++;
            _log.LogInformation("[Retention] delete-after-watched removed '{Title}' ({Gb:0.00} GB)", title, bytes / 1_000_000_000.0);
        }
        return deleted;
    }

    /// <summary>Pick the eviction candidates for one league's items (newest-first) under a mode. Protected items are
    /// excluded BEFORE this runs (RET-01) — keep-N slots and the GB budget only ever count unprotected items.</summary>
    private static (List<LibraryItem> Victims, string Detail) SelectVictims(string mode, List<LibraryItem> itemsDesc,
        int keepLast, int keepDays, int gbCap, long now, WatchedRetentionConfig watchedCfg)
    {
        switch (mode)
        {
            case "keep_last_n":
            {
                var n = Math.Max(0, keepLast);
                return (itemsDesc.Skip(n).ToList(), $"keep the {n} most recent");
            }
            case "keep_days":
            {
                var days = Math.Max(0, keepDays);
                var cutoff = now - (long)days * 86400;
                return (itemsDesc.Where(i => i.StartUtc < cutoff).ToList(), $"keep the last {days} days");
            }
            case "gb_cap":
            {
                var capGb = Math.Max(0, gbCap);
                var cap = (long)capGb * 1_000_000_000L;
                long used = 0; var victims = new List<LibraryItem>();
                foreach (var i in itemsDesc) // newest first; oldest fall past the cap first
                {
                    used += i.FileBytes;
                    if (used > cap) victims.Add(i);
                }
                return (victims, $"keep the newest {capGb} GB");
            }
            case "watched":
                // Only games PAST their safety window (estimated finish + buffer) — never a game the viewer may still
                // be finishing. Mirrors the per-tick EvictWatchedDueAsync so the dry-run preview and the sweep agree.
                return (itemsDesc.Where(i => i.WatchedUtc is long w && now >= WatchedDeleteEarliest(w, i.DurationS, watchedCfg)).ToList(), "delete after watched");
            default:
                return (new List<LibraryItem>(), "keep everything");
        }
    }
}
