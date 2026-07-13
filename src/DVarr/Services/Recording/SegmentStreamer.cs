namespace DVarr.Services.Recording;

/// <summary>
/// Streams a recording's captured MPEG-TS segments off the local disk in capture order, tailing the segment
/// ffmpeg is still writing and rolling onto new segments as they appear — the "watch it while it records"
/// reader. Pure file reads: it never contacts the provider and needs no tuner slot. Ordinal filename sort is
/// capture order by construction (seg-%Y%m%d-%H%M%S-L### — the launch counter breaks same-second ties).
/// </summary>
public static class SegmentStreamer
{
    /// <summary>Closed + in-progress segment files for a recording, in capture order.</summary>
    public static IReadOnlyList<string> ListSegments(string segDir)
    {
        try
        {
            if (!Directory.Exists(segDir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(segDir, "seg-*.ts")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Copy the segments to <paramref name="dest"/>: everything already on disk, then keep following while
    /// <paramref name="isLive"/> stays true (tail the growing file; hop to each new segment as it appears).
    /// When not/no-longer live, drains what exists and returns. <paramref name="fromStart"/>=false starts at
    /// the second-newest segment (a live-edge tail) instead of the beginning.
    /// Ends cleanly on cancellation, on the destination closing, or when a finished recording is fully drained.
    /// </summary>
    public static async Task StreamAsync(string segDir, bool fromStart, Func<bool> isLive, Stream dest, CancellationToken ct)
    {
        var files = ListSegments(segDir);
        // A recording can be Starting with no segment on disk yet — give it a short window to produce one.
        for (var wait = 0; files.Count == 0 && isLive() && wait < 40 && !ct.IsCancellationRequested; wait++)
        {
            await Task.Delay(500, ct);
            files = ListSegments(segDir);
        }
        if (files.Count == 0) return;

        var idx = fromStart ? 0 : Math.Max(0, files.Count - 2);
        var buf = new byte[256 * 1024];

        while (!ct.IsCancellationRequested)
        {
            if (idx >= files.Count)
            {
                if (!isLive()) return; // finished and fully drained
                try { await Task.Delay(500, ct); } catch { return; }
                files = ListSegments(segDir);
                if (files.Count == 0 && !Directory.Exists(segDir)) return; // finalize already cleaned up
                continue;
            }

            var path = files[idx];
            FileStream? fs = null;
            try
            {
                // ReadWrite|Delete share: ffmpeg is still appending, and finalize may delete the dir under us
                // (harmless — we drain the open handle and stop at the isLive check).
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buf.Length, useAsync: true);
            }
            catch
            {
                idx++; // vanished/unreadable segment — skip rather than abort the whole preview
                continue;
            }

            await using (fs)
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await fs.ReadAsync(buf.AsMemory(), ct);
                    if (n > 0)
                    {
                        await dest.WriteAsync(buf.AsMemory(0, n), ct);
                        continue;
                    }
                    // At EOF-for-now. If a newer segment exists, this one is closed — move on. Otherwise it's
                    // the live tail: wait for growth (or for the recording to end).
                    files = ListSegments(segDir);
                    if (files.Count - 1 > idx) break;
                    if (!isLive())
                    {
                        // Recording ended — one final drain in case the last flush landed after our read.
                        var tail = await fs.ReadAsync(buf.AsMemory(), ct);
                        if (tail > 0) await dest.WriteAsync(buf.AsMemory(0, tail), ct);
                        else break;
                        continue;
                    }
                    try { await Task.Delay(300, ct); } catch { return; }
                }
            }
            if (ct.IsCancellationRequested) return;
            idx++;
            if (idx >= files.Count && !isLive()) return;
        }
    }
}
