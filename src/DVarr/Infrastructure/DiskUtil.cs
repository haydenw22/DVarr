using DVarr.Data;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Infrastructure;

/// <summary>Free/total bytes of a filesystem.</summary>
public readonly record struct DiskInfo(long FreeBytes, long TotalBytes);

/// <summary>Filesystem free-space reads. Longest-mount-point match so a container volume (/media, /segments)
/// reports ITS backing store, not the host root.</summary>
public static class DiskUtil
{
    private static DriveInfo? BestDrive(string path)
    {
        // Trailing-separator on both sides so "/media" matches the "/media" mount and not just "/"; longest match wins.
        static string Sep(string p) => p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
        var full = Sep(Path.GetFullPath(path));
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && full.StartsWith(Sep(d.RootDirectory.FullName), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.RootDirectory.FullName.Length)
            .FirstOrDefault();
    }

    /// <summary>Free/total bytes of the filesystem backing <paramref name="path"/>, or null when it can't be read.</summary>
    public static DiskInfo? For(string path)
    {
        try
        {
            var d = BestDrive(path);
            return d is null ? null : new DiskInfo(d.AvailableFreeSpace, d.TotalSize);
        }
        catch { return null; }
    }

    /// <summary>The mount-point root backing <paramref name="path"/> (to tell whether two dirs share a filesystem),
    /// or null when it can't be read.</summary>
    public static string? MountRoot(string path)
    {
        try { return BestDrive(path)?.RootDirectory.FullName; }
        catch { return null; }
    }
}

/// <summary>Disk-space guardrails: project a recording's on-disk size from its channel's history and warn when a
/// recording (or the current free space) would fall below the configured media/segments floors. Warn-only — a
/// rough bitrate estimate must never silently drop a game.</summary>
public static class DiskGuard
{
    // Fallback average stream rates (bytes/sec) by quality tier, for a channel with no finished recordings yet.
    // Deliberately generous so a first projection over-reserves rather than under-warns.
    private static long TierBytesPerSecond(string? quality) => (quality switch
    {
        "2160p" => 25_000L,           // ~25 Mbps 4K
        "1080p" => 8_000L,            // ~8 Mbps
        "720p" => 5_000L,             // ~5 Mbps
        _ => 4_000L,                  // SD / unknown
    }) * 1000 / 8;

    /// <summary>Mean captured bytes/second for a channel from its own finished library items (bytes ÷ probed
    /// duration), falling back to a per-tier default when it has no history.</summary>
    public static async Task<long> EstimateBytesPerSecondAsync(DVarrDbContext db, int channelId, string? quality, CancellationToken ct = default)
    {
        var chanRecIds = db.Recordings.AsNoTracking().Where(r => r.ChannelId == channelId).Select(r => r.Id);
        var rows = await db.LibraryItems.AsNoTracking()
            .Where(i => i.RecordingId != null && i.DurationS > 0 && i.FileBytes > 0 && chanRecIds.Contains(i.RecordingId!.Value))
            .OrderByDescending(i => i.Id).Take(20)
            .Select(i => new { i.FileBytes, i.DurationS })
            .ToListAsync(ct);
        if (rows.Count > 0)
        {
            var avg = rows.Average(r => (double)r.FileBytes / r.DurationS!.Value);
            if (avg > 0) return (long)avg;
        }
        return TierBytesPerSecond(quality);
    }

    /// <summary>Projected on-disk bytes of a recording spanning <paramref name="windowSeconds"/> on a channel.</summary>
    public static async Task<long> ProjectBytesAsync(DVarrDbContext db, int channelId, string? quality, long windowSeconds, CancellationToken ct = default)
        => Math.Max(0, windowSeconds) * await EstimateBytesPerSecondAsync(db, channelId, quality, ct);

    /// <summary>Warnings for a projected recording that would push a filesystem below its floor. When media and
    /// segments share a volume, both copies briefly coexist at finalize (~2×). Empty = all clear / floors off.</summary>
    public static List<string> ProjectedWarnings(long projectedBytes, RuntimePaths paths, long mediaFloorBytes, long segFloorBytes)
    {
        var warnings = new List<string>();
        if (SameVolume(paths))
            AddWarning(warnings, "Storage", paths.MediaDir, Math.Max(mediaFloorBytes, segFloorBytes), projectedBytes * 2);
        else
        {
            AddWarning(warnings, "Media", paths.MediaDir, mediaFloorBytes, projectedBytes);
            AddWarning(warnings, "Segments", paths.SegmentDir, segFloorBytes, projectedBytes);
        }
        return warnings;
    }

    /// <summary>Warnings for filesystems ALREADY under their floor right now (no pending recording).</summary>
    public static List<string> CurrentLowSpace(RuntimePaths paths, long mediaFloorBytes, long segFloorBytes)
    {
        var warnings = new List<string>();
        AddWarning(warnings, "Media", paths.MediaDir, mediaFloorBytes, 0);
        if (!SameVolume(paths))
            AddWarning(warnings, "Segments", paths.SegmentDir, segFloorBytes, 0);
        return warnings;
    }

    private static bool SameVolume(RuntimePaths paths)
    {
        var m = DiskUtil.MountRoot(paths.MediaDir);
        var s = DiskUtil.MountRoot(paths.SegmentDir);
        return m != null && string.Equals(m, s, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddWarning(List<string> w, string label, string dir, long floorBytes, long needBytes)
    {
        if (floorBytes <= 0) return;
        var d = DiskUtil.For(dir);
        if (d is null) return;
        var after = d.Value.FreeBytes - needBytes;
        if (after < floorBytes)
            w.Add(needBytes > 0
                ? $"{label} would drop to ~{Gb(Math.Max(0, after))} GB free after this recording (~{Gb(needBytes)} GB), below the {Gb(floorBytes)} GB floor"
                : $"{label} is at ~{Gb(d.Value.FreeBytes)} GB free, below the {Gb(floorBytes)} GB floor");
    }

    private static string Gb(long bytes) => (bytes / 1_000_000_000.0).ToString("0.0");
}
