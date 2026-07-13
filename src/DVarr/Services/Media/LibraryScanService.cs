using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Media;

public sealed record LibraryScanSummary(int Seen, int Adopted, int Healed, int MarkedMissing, long TookMs, string? Skipped);

/// <summary>
/// The library's reconciler: keeps LibraryItem rows in sync with what is physically on the media drive.
/// Adopts video files DVarr didn't create (or lost track of), marks items whose file vanished as Missing
/// (healing them if the file comes back, including a move/rename heal by size+name), and backfills library
/// rows for pre-library Done recordings on first boot. Runs at startup, on a 6-hour interval, and on demand
/// from the Library page. Singleton + hosted.
/// </summary>
public sealed class LibraryScanService : BackgroundService
{
    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".ts", ".m4v", ".avi", ".mov", ".webm" };
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    /// <summary>An untracked file modified this recently is skipped — it's likely a finalize writing its concat
    /// output (flat in the media root) that the import is about to move; adopting it would create a ghost row.</summary>
    private const int InFlightGraceS = 120;
    private const int MaxProbesPerScan = 50;

    private readonly IServiceScopeFactory _scopes;
    private readonly RuntimePaths _paths;
    private readonly ILogger<LibraryScanService> _log;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public long LastScanUtc { get; private set; }
    public LibraryScanSummary? LastSummary { get; private set; }

    public LibraryScanService(IServiceScopeFactory scopes, RuntimePaths paths, ILogger<LibraryScanService> log)
    { _scopes = scopes; _paths = paths; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let boot recovery / migrations settle before the first pass.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch { return; }
        try { await BackfillFromRecordingsAsync(stoppingToken); }
        catch (Exception ex) { _log.LogWarning(ex, "[LibraryScan] backfill failed (will not retry — the scan still adopts the files)"); }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "[LibraryScan] scan failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { break; }
        }
    }

    /// <summary>One-time adoption of history from before the library existed: every Done recording whose file is
    /// still on disk gets a library row with full provenance (channel/source/event) that a bare disk scan couldn't
    /// recover. Idempotent — the upsert refreshes rather than duplicates.</summary>
    private async Task BackfillFromRecordingsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
        var lib = scope.ServiceProvider.GetRequiredService<LibraryService>();
        var done = await db.Recordings.AsNoTracking()
            .Where(r => r.State == RecordingState.Done && r.OutputPath != null && r.OutputPath != "")
            .Select(r => new { r.Id, r.OutputPath }).ToListAsync(ct);
        var n = 0;
        foreach (var r in done)
        {
            if (ct.IsCancellationRequested) return;
            if (!File.Exists(r.OutputPath!)) continue;
            await lib.UpsertForRecordingAsync(r.Id, r.OutputPath!, ct);
            n++;
        }
        if (n > 0) _log.LogInformation("[LibraryScan] backfilled {N} library item(s) from finished recordings", n);
    }

    /// <summary>Run one reconciling pass now (used by the interval loop and POST /api/library/scan).
    /// Serialized — a second caller waits for the running pass and then runs its own.</summary>
    public async Task<LibraryScanSummary> ScanAsync(CancellationToken ct)
    {
        await _scanGate.WaitAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var lib = scope.ServiceProvider.GetRequiredService<LibraryService>();
            var gate = scope.ServiceProvider.GetRequiredService<DbWriteGate>();

            // HARD GUARD: if the media root is missing/unreadable (unmounted share, dead disk), do NOTHING —
            // especially not the Missing sweep, which would otherwise flag the entire library over a hiccup.
            List<string> files;
            try
            {
                if (!Directory.Exists(_paths.MediaDir))
                    return Skip("media folder not found — is the drive mounted?");
                files = EnumerateVideos(_paths.MediaDir).ToList();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[LibraryScan] media folder unreadable — scan skipped");
                return Skip($"media folder unreadable ({ex.Message})");
            }

            var items = await db.LibraryItems.AsNoTracking().ToListAsync(ct);
            // GroupBy, not ToDictionary: the unique index is case-sensitive, so two rows can differ only by
            // case — a collision here must not crash the whole scan.
            var byPath = items.GroupBy(i => Norm(i.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Paths that belong to captures still in flight — never adopt those (finalize owns them).
            var nonTerminal = new[] { RecordingState.Pending, RecordingState.Starting, RecordingState.Recording,
                RecordingState.Recovering, RecordingState.FailingOver, RecordingState.Degraded,
                RecordingState.Stopping, RecordingState.Finalizing, RecordingState.FinalizeRetry };
            var inFlight = new HashSet<string>(
                (await db.Recordings.AsNoTracking().Where(r => nonTerminal.Contains(r.State) && r.OutputPath != null)
                    .Select(r => r.OutputPath!).ToListAsync(ct)).Select(Norm),
                StringComparer.OrdinalIgnoreCase);

            var seenIds = new HashSet<int>();
            var adopted = 0; var healed = 0; var probes = 0;
            var now = EpochTime.Now();

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;
                var key = Norm(file);
                if (inFlight.Contains(key)) continue;

                if (byPath.TryGetValue(key, out var known))
                {
                    seenIds.Add(known.Id);
                    var len = SafeLength(file);
                    if (known.Status == LibraryItemStatus.Missing || known.FileBytes != len)
                    {
                        if (known.Status == LibraryItemStatus.Missing) healed++;
                        await gate.WriteAsync(async () =>
                        {
                            var row = await db.LibraryItems.FindAsync(new object?[] { known.Id }, ct);
                            if (row is null) return;
                            row.Status = LibraryItemStatus.Ok; row.MissingSinceUtc = null;
                            row.FileBytes = len; row.UpdatedUtc = EpochTime.Now();
                            await db.SaveChangesAsync(ct);
                        }, ct);
                    }
                    continue;
                }

                // Untracked file. Give an in-flight finalize its grace window before adopting.
                if (now - SafeMtimeUtc(file) < InFlightGraceS) continue;

                // Move/rename heal: a Missing item with the same basename + byte size is this file relocated —
                // re-point the existing row (keeping its provenance) instead of minting an amnesiac duplicate.
                var length = SafeLength(file);
                var moved = items.FirstOrDefault(i => i.Status == LibraryItemStatus.Missing && !seenIds.Contains(i.Id)
                    && i.FileBytes == length && i.FileBytes > 0
                    && string.Equals(Path.GetFileName(i.FilePath), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
                if (moved is not null)
                {
                    seenIds.Add(moved.Id);
                    healed++;
                    var parsedMove = lib.ParsePath(file);
                    await gate.WriteAsync(async () =>
                    {
                        var row = await db.LibraryItems.FindAsync(new object?[] { moved.Id }, ct);
                        if (row is null) return;
                        row.FilePath = file; row.Status = LibraryItemStatus.Ok; row.MissingSinceUtc = null;
                        row.Unsorted = parsedMove.Unsorted; row.UpdatedUtc = EpochTime.Now();
                        await db.SaveChangesAsync(ct);
                    }, ct);
                    _log.LogInformation("[LibraryScan] healed moved file → {Path}", file);
                    continue;
                }

                // Adopt: reconstruct what we can from the path, probe the file, and best-effort link a league/event.
                var parsed = lib.ParsePath(file);
                var probe = probes < MaxProbesPerScan ? await lib.ProbeAsync(file, ct) : new MediaInfo(null, null, null, null);
                if (probes++ == MaxProbesPerScan)
                    _log.LogInformation("[LibraryScan] probe cap ({Cap}) reached — remaining adoptions get codec detail on a later scan", MaxProbesPerScan);

                var (leagueId, eventId, sport, evStart) = await lib.MatchLeagueEventAsync(parsed, ct);

                await lib.UpsertAsync(new LibraryItem
                {
                    RecordingId = null,
                    EventId = eventId,
                    LeagueId = leagueId,
                    ShowName = parsed.ShowName ?? "",
                    Sport = sport,
                    SeasonYear = parsed.SeasonYear,
                    EpisodeNum = parsed.EpisodeNum,
                    Title = parsed.Title,
                    StartUtc = evStart ?? parsed.StartUtc ?? SafeMtimeUtc(file),
                    FilePath = file,
                    FileBytes = length,
                    DurationS = probe.DurationS,
                    VideoCodec = probe.VideoCodec,
                    AudioCodec = probe.AudioCodec,
                    Height = probe.Height ?? parsed.Height,
                    Origin = LibraryItemOrigin.Adopted,
                    Status = LibraryItemStatus.Ok,
                    Unsorted = parsed.Unsorted,
                }, ct);
                adopted++;
            }

            // Missing sweep — only reached when the media root enumerated successfully above.
            var markedMissing = 0;
            if (!ct.IsCancellationRequested)
            {
                var missingIds = items.Where(i => !seenIds.Contains(i.Id) && i.Status == LibraryItemStatus.Ok
                        && !File.Exists(i.FilePath)) // per-file recheck: races with a finalize that landed mid-scan
                    .Select(i => i.Id).ToList();
                foreach (var id in missingIds)
                {
                    await gate.WriteAsync(async () =>
                    {
                        var row = await db.LibraryItems.FindAsync(new object?[] { id }, ct);
                        if (row is null || row.Status == LibraryItemStatus.Missing || File.Exists(row.FilePath)) return;
                        row.Status = LibraryItemStatus.Missing; row.MissingSinceUtc = EpochTime.Now();
                        row.UpdatedUtc = EpochTime.Now();
                        await db.SaveChangesAsync(ct);
                    }, ct);
                    markedMissing++;
                }
                if (markedMissing > 0)
                    _log.LogWarning("[LibraryScan] {N} library file(s) are no longer on disk (marked Missing — they heal if the files return)", markedMissing);
            }

            var summary = new LibraryScanSummary(files.Count, adopted, healed, markedMissing, sw.ElapsedMilliseconds, null);
            LastScanUtc = EpochTime.Now();
            LastSummary = summary;
            _log.LogInformation("[LibraryScan] pass complete: {Seen} file(s), {Adopted} adopted, {Healed} healed, {Missing} missing ({Ms} ms)",
                summary.Seen, summary.Adopted, summary.Healed, summary.MarkedMissing, summary.TookMs);
            return summary;
        }
        finally { _scanGate.Release(); }

        LibraryScanSummary Skip(string reason)
        {
            var s = new LibraryScanSummary(0, 0, 0, 0, sw.ElapsedMilliseconds, reason);
            LastSummary = s;
            return s;
        }
    }

    /// <summary>Video files under the media root, skipping dot-folders (except our own ".unsorted" staging),
    /// anything under the segment scratch if it happens to nest inside media, and non-video extensions.</summary>
    private IEnumerable<string> EnumerateVideos(string root)
    {
        var segRoot = Norm(_paths.SegmentDir);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') && !string.Equals(name, ".unsorted", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Norm(sub), segRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "@eaDir", StringComparison.OrdinalIgnoreCase)) continue; // Synology detritus
                stack.Push(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
                if (VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    yield return f;
        }
    }

    private static string Norm(string p)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)); } catch { return p; }
    }

    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    private static long SafeMtimeUtc(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds(); } catch { return 0; }
    }
}
