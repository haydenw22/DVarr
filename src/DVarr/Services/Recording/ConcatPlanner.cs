using System.Diagnostics;
using System.Globalization;

namespace DVarr.Services.Recording;

/// <summary>A run of packet timestamps on ONE clock inside a segment (split wherever consecutive sorted PTS are more
/// than <see cref="ConcatPlanner.ClusterGapS"/> apart).</summary>
public readonly record struct PtsCluster(double Min, double Max, int Count);

/// <summary>
/// What a segment's packets look like, per stream type. <see cref="Clusters"/>/<see cref="HeadCluster"/> describe the
/// first VIDEO stream (the one finalize maps); <see cref="AudioClusters"/> describe every audio packet. A healthy
/// segment has one video cluster and audio sitting inside the same span; anything else is a clock discontinuity.
/// </summary>
public sealed record SegmentProbe(bool Ok, double Min, double Max, IReadOnlyList<PtsCluster> Clusters, int HeadCluster,
    IReadOnlyList<PtsCluster> AudioClusters)
{
    public static readonly SegmentProbe Unreadable = new(false, 0, 0, Array.Empty<PtsCluster>(), -1, Array.Empty<PtsCluster>());

    /// <summary>Parse <c>ffprobe -show_entries packet=codec_type,stream_index,pts_time -of csv=p=0</c> output.
    /// Lines look like <c>video,0,82284.423222,</c> — ffprobe 7/8 appends a trailing comma when a packet carries side
    /// data, and stream indices follow discovery order (an audio packet that leads the file becomes stream 0), so the
    /// video stream is identified by codec type, never by index.</summary>
    public static SegmentProbe Parse(string csv)
    {
        var inv = CultureInfo.InvariantCulture;
        int? videoIdx = null;
        var video = new List<double>();
        var audio = new List<double>();
        foreach (var raw in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(',');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, inv, out var pts)) continue; // "N/A" etc.
            if (parts[0] == "video" && int.TryParse(parts[1], NumberStyles.Integer, inv, out var si))
            {
                videoIdx ??= si;
                if (si != videoIdx) { if (si < videoIdx) { videoIdx = si; video.Clear(); } else continue; }
                video.Add(pts);
            }
            else if (parts[0] == "audio") audio.Add(pts);
        }
        if (video.Count == 0) return Unreadable;
        var clusters = Cluster(video);
        var head = video[0];
        var headIdx = -1;
        for (var i = 0; i < clusters.Count; i++)
            if (head >= clusters[i].Min - 1e-6 && head <= clusters[i].Max + 1e-6) { headIdx = i; break; }
        return new SegmentProbe(true, clusters.Min(c => c.Min), clusters.Max(c => c.Max), clusters, headIdx,
            audio.Count == 0 ? Array.Empty<PtsCluster>() : Cluster(audio));
    }

    private static List<PtsCluster> Cluster(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var clusters = new List<PtsCluster>();
        double start = sorted[0], prev = sorted[0];
        var count = 1;
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - prev > ConcatPlanner.ClusterGapS)
            {
                clusters.Add(new PtsCluster(start, prev, count));
                start = sorted[i];
                count = 0;
            }
            prev = sorted[i];
            count++;
        }
        clusters.Add(new PtsCluster(start, prev, count));
        return clusters;
    }

    /// <summary>True when some audio sits on a different clock from the video window [lo, hi] — more than a
    /// cluster gap outside it. The concat demuxer sizes and offsets each file from ALL its streams, so a stray
    /// half-second of audio on a restarted (or wrapped) clock turns one 8s segment into a ~13,000s "file" and
    /// throws every following segment hours down the timeline (the "7h recording that stops at 2:30" bug).</summary>
    public bool AudioOffClock(double lo, double hi)
        => AudioClusters.Any(a => a.Max < lo - ConcatPlanner.ClusterGapS || a.Min > hi + ConcatPlanner.ClusterGapS);
}

/// <summary>A segment rewritten to keep only one window of its timeline. Its clock is NOT the original one (ffmpeg
/// re-zeros it), so <see cref="VMin"/>/<see cref="VMax"/> are the rewritten file's own video span — used to translate
/// any in-point computed on the original clock.</summary>
public sealed record RewriteResult(string Path, double VMin, double VMax);

/// <summary>The concat list plus what the planner did to get there (for logs, notifications and the length guards).</summary>
public sealed class ConcatPlan
{
    public List<string> Lines { get; } = new() { "ffconcat version 1.0" };
    public int Kept { get; internal set; }
    public int Trimmed { get; internal set; }
    public int Dropped { get; internal set; }
    public int JumpDropped { get; internal set; }
    public int Rebased { get; internal set; }
    public int Salvaged { get; internal set; }
    public int CrossStreamFixed { get; internal set; }
    /// <summary>Seconds of video the kept segments hold, from their probed spans (8s for an unreadable segment).
    /// Drives the over-long guard: a finished file far longer than this has a broken timeline.</summary>
    public double ContentS { get; internal set; }
}

/// <summary>
/// Builds the ffconcat list that turns a recording's 8-second TS segments into one file. Two jobs:
///
/// 1. TIMELINE INTEGRITY (always, live and catch-up): every kept file must be internally on one clock. The concat
///    demuxer rebases each file from its own start and sizes it from its own duration — both computed across ALL
///    streams — so a segment whose audio straggles onto another clock, or whose video jumps clocks mid-file, corrupts
///    the offset of everything after it. Such segments are rewritten to keep one clock's window
///    (<see cref="RewriteResult"/>); a video clock jump keeps the cluster that continues the timeline.
/// 2. DE-OVERLAP (live captures only): a provider that re-serves buffered seconds on reconnect leaves PTS regressing
///    below the running timeline — wholly-duplicate segments are dropped, partial overlaps trimmed with an `inpoint`,
///    and a segment far below the timeline (a restarted clock) is kept and rebases it. Catch-up pulls skip this:
///    every archive chunk restarts its clock by design, and nothing in them is a re-served duplicate.
///
/// Pure apart from the injected rewrite callback, so the decisions are unit-testable against fake timelines.
/// </summary>
public static class ConcatPlanner
{
    /// <summary>Longest internal video PTS span (s) a single segment can legitimately have. Segments are cut every 8s
    /// of wall clock (plus up to a GOP), so minutes of internal span always means a mid-file clock jump.</summary>
    public const double MaxSaneSegmentSpanS = 300;

    /// <summary>How far BELOW the running timeline a segment can sit and still be a genuine duplicate. A provider
    /// re-serving buffered content on reconnect only replays its last few seconds; anything further back is a
    /// restarted source clock (or a 33-bit PTS wrap) and must rebase the timeline, not be discarded.</summary>
    public const double MaxReserveLookbackS = 30;

    /// <summary>Consecutive whole-segment duplicate drops before the pass concludes it's looking at a restarted clock
    /// rather than provider re-serving, and rebases instead of deleting more footage.</summary>
    public const int MaxConsecutiveDuplicateDrops = 4;

    /// <summary>Smallest PTS gap inside ONE segment that marks a clock discontinuity rather than normal packet spacing.
    /// A segment covers ~8s of wall clock, so a 30s internal gap is never legitimate.</summary>
    public const double ClusterGapS = 30;

    /// <summary>A jump segment's chosen cluster must hold at least this fraction of its packets to be worth
    /// salvaging — below it the file is mostly the other clock and the rewrite isn't worth the risk.</summary>
    public const double MinSalvageFrac = 0.25;

    /// <summary>Most distinct clocks a salvageable segment may contain. More than this is a pathological file.</summary>
    public const int MaxSalvageClusters = 4;

    /// <summary>Margin (s) either side of a kept window when rewriting. The window is measured on PRESENTATION
    /// timestamps, but a stream-copy cut filters on DECODE timestamps — without slack the keyframe that opens the
    /// window (DTS just below its PTS) is discarded and a second of picture goes with it. Any other clock is at least
    /// <see cref="ClusterGapS"/> away, so the slack can never pull it back in.</summary>
    public const double RewriteMarginS = 2;

    private const double Eps = 0.05;
    private const double NominalSegmentS = 8;

    public static async Task<ConcatPlan> BuildAsync(IReadOnlyList<string> segs, IReadOnlyList<SegmentProbe> probes, bool dedupe,
        Func<string, double, double, Task<RewriteResult?>> rewrite, ILogger log)
    {
        var plan = new ConcatPlan();
        var inv = CultureInfo.InvariantCulture;
        double timelineEnd = double.NegativeInfinity;
        var dupRun = 0;

        void Add(string path, double contentS, double? inpoint = null, double? outpoint = null)
        {
            plan.Lines.Add(FileLine(path));
            if (inpoint is { } ip) plan.Lines.Add($"inpoint {ip.ToString(inv)}");
            if (outpoint is { } op) plan.Lines.Add($"outpoint {op.ToString(inv)}");
            plan.Kept++;
            plan.ContentS += Math.Max(0, contentS);
        }

        // Keep a segment whose VIDEO is on one clock, rewriting it first when its audio isn't. `inpoint` is on the
        // ORIGINAL clock and is translated onto the rewritten file's clock.
        async Task KeepAsync(string seg, SegmentProbe p, double? inpoint)
        {
            var span = p.Max - (inpoint ?? p.Min);
            if (p.AudioOffClock(p.Min, p.Max))
            {
                var rw = await rewrite(seg, Math.Max(0, p.Min - RewriteMarginS), p.Max + RewriteMarginS);
                if (rw is not null)
                {
                    plan.CrossStreamFixed++;
                    Add(rw.Path, span, inpoint is { } ip ? ip - p.Min + rw.VMin : null);
                    return;
                }
                log.LogWarning("[Recorder] segment {Seg} carries audio on another clock and couldn't be rewritten — including it as-is (the file's length may read wrong)",
                    Path.GetFileName(seg));
            }
            Add(seg, span, inpoint);
        }

        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var p = i < probes.Count ? probes[i] : SegmentProbe.Unreadable;
            if (!p.Ok)
            {
                // Unreadable PTS → include verbatim and reset the guard (never worse than the old behaviour).
                Add(s, NominalSegmentS);
                timelineEnd = double.NegativeInfinity;
                continue;
            }

            if (p.Max - p.Min > MaxSaneSegmentSpanS)
            {
                // Intra-segment clock jump: ffmpeg's -reconnect spliced a connection that restarted its PCR/PTS into
                // the SAME file. Keep the cluster that continues the running timeline (else the biggest) and drop the
                // other clock's packets; only a genuinely unusable segment is dropped whole.
                var kept = await SalvageAsync(s, p, timelineEnd, rewrite);
                if (kept is { } k)
                {
                    plan.Salvaged++;
                    Add(k.Path, k.Cluster.Max - k.Cluster.Min, outpoint: k.Outpoint);
                    // Track the timeline on the ORIGINAL clock: the next segment continues the kept clock, not the
                    // rewritten file's re-zeroed one (which defeated the "continue the timeline" preference above).
                    timelineEnd = k.Cluster.Max;
                    dupRun = 0;
                }
                else
                {
                    plan.JumpDropped++;
                    log.LogWarning("[Recorder] segment {Seg} spans {Span:0.#}s internally (clock jump inside the file) and couldn't be salvaged — dropped from finalize",
                        Path.GetFileName(s), p.Max - p.Min);
                }
                continue;
            }

            if (!dedupe || double.IsNegativeInfinity(timelineEnd) || p.Min >= timelineEnd - Eps)
            {
                await KeepAsync(s, p, null);              // no overlap (or catch-up: chunks restart their clock by design)
                timelineEnd = p.Max;
                dupRun = 0;
            }
            else if (timelineEnd - p.Max > MaxReserveLookbackS)
            {
                // The WHOLE segment sits far below the running timeline: not a re-served duplicate (a provider only
                // re-serves its last few buffered seconds) but a NEW CLOCK — keep it and rebase the timeline.
                plan.Rebased++;
                log.LogWarning("[Recorder] segment {Seg} restarts the clock (ends {Max:0.#}s, timeline was at {End:0.#}s) — rebasing the finalize timeline instead of dropping",
                    Path.GetFileName(s), p.Max, timelineEnd);
                await KeepAsync(s, p, null);
                timelineEnd = p.Max;
                dupRun = 0;
            }
            else if (p.Max <= timelineEnd + Eps && dupRun < MaxConsecutiveDuplicateDrops)
            {
                // Wholly inside the timeline already → a re-served duplicate; drop it. Bounded by dupRun: a long run
                // of whole-segment "duplicates" is really two clocks oscillating under the lookback threshold.
                plan.Dropped++;
                dupRun++;
                log.LogDebug("[Recorder] segment {Seg} [{Min:0.#}..{Max:0.#}] sits inside the timeline ({End:0.#}) — dropped as duplicate",
                    Path.GetFileName(s), p.Min, p.Max, timelineEnd);
            }
            else if (p.Max <= timelineEnd + Eps)
            {
                plan.Rebased++;
                log.LogWarning("[Recorder] segment {Seg} is the {N}th consecutive whole-segment duplicate — treating it as a restarted clock and rebasing instead of dropping more footage",
                    Path.GetFileName(s), dupRun + 1);
                await KeepAsync(s, p, null);
                timelineEnd = p.Max;
                dupRun = 0;
            }
            else
            {
                plan.Trimmed++;                           // partial overlap → skip the duplicate head
                await KeepAsync(s, p, timelineEnd);
                timelineEnd = p.Max;
                dupRun = 0;
            }
        }

        // Pathological source: if the pass would leave NOTHING, fall back to the plain list — a file with a weird
        // timeline still beats no recording at all.
        if (segs.Count > 0 && plan.Kept == 0)
        {
            log.LogWarning("[Recorder] finalize planning would have discarded ALL {N} segment(s) — falling back to the plain concat list", segs.Count);
            var plain = new ConcatPlan();
            foreach (var s in segs) { plain.Lines.Add(FileLine(s)); plain.Kept++; plain.ContentS += NominalSegmentS; }
            return plain;
        }
        return plan;
    }

    private readonly record struct Salvage(string Path, double? Outpoint, PtsCluster Cluster);

    /// <summary>Keep ONE video clock's packets from a segment whose internal span is impossibly long. Prefers the
    /// cluster that continues the running timeline (else the one covering the most time), then rewrites that window
    /// (timestamp-filtered, seek-free, all streams). If the rewrite fails and the kept cluster is the one the file
    /// OPENS with, an `outpoint` just past its end is the fallback (sequential read from byte 0). Null = unusable.</summary>
    private static async Task<Salvage?> SalvageAsync(string seg, SegmentProbe probe, double timelineEnd,
        Func<string, double, double, Task<RewriteResult?>> rewrite)
    {
        if (probe.Clusters.Count < 2 || probe.Clusters.Count > MaxSalvageClusters) return null;
        var totalPackets = probe.Clusters.Sum(c => c.Count);
        if (totalPackets <= 0) return null;

        var pick = probe.Clusters
            .Select((c, idx) => (c, idx))
            .OrderByDescending(x => !double.IsNegativeInfinity(timelineEnd)
                                    && x.c.Min >= timelineEnd - ClusterGapS && x.c.Min <= timelineEnd + ClusterGapS)
            .ThenByDescending(x => x.c.Max - x.c.Min)
            .ThenByDescending(x => x.c.Count)
            .First();
        if (pick.c.Max - pick.c.Min > MaxSaneSegmentSpanS) return null;            // the cluster itself is broken
        if ((double)pick.c.Count / totalPackets < MinSalvageFrac) return null;     // mostly the other clock

        var rw = await rewrite(seg, Math.Max(0, pick.c.Min - RewriteMarginS), pick.c.Max + RewriteMarginS);
        if (rw is not null) return new Salvage(rw.Path, null, pick.c);
        if (pick.idx == probe.HeadCluster) return new Salvage(seg, pick.c.Max + 0.2, pick.c);
        return null;
    }

    public static string FileLine(string s) => $"file '{s.Replace("\\", "/").Replace("'", "'\\''")}'";

    /// <summary>The finalize ffmpeg arguments (concat list → one MKV). Shared with the tests so they exercise exactly
    /// what production runs.</summary>
    public static List<string> FinalizeArgs(string listPath, string? chaptersPath, string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "concat", "-safe", "0", "-i", listPath,
        };
        if (chaptersPath is not null) args.AddRange(new[] { "-f", "ffmetadata", "-i", chaptersPath });
        args.AddRange(new[]
        {
            "-map", "0:v:0", "-map", "0:a:0?",
            // VIDEO stays LOSSLESS copy; the setts BSF rewrites only a rogue >10s PTS outlier (no-op on healthy
            // frames), so a corrupt source timestamp can't inflate the container to bogus hours. DROPPED +genpts:
            // it can't repair a present-but-wrong PTS and extrapolates across source discontinuities.
            "-c:v", "copy", "-bsf:v", "setts=pts=if(gt(abs(PTS-DTS)\\,900000)\\,DTS\\,PTS)",
            // AUDIO is re-encoded to ONE uniform AAC track (cheap CPU, NO NVENC). IPTV pre-show vs main feed
            // frequently use different audio codecs/SBR configs; concatenating them under -c copy leaves ~1 min
            // undecodable (silent) at the splice. A uniform re-encode eliminates that whole class of audio dropouts.
            // aresample async keeps audio locked to the (de-overlapped) video timeline across reconnect splices
            // instead of leaving a silent gap; make_zero rebases any small negative TS the inpoint seeks introduce.
            // async=1000 (was 1): the number is how many samples/sec may be SOFT-corrected by resampling. At 1 that
            // is effectively nothing, so every correction fell through to HARD compensation — inserting silence or
            // dropping samples once drift passed min_hard_comp — which is audible as a brief dropout every 15-20s on
            // any feed whose audio and video clocks drift apart (common on IPTV restreams). Allowing generous soft
            // correction absorbs that drift inaudibly, while min_hard_comp stays as the safety net for genuine large
            // discontinuities at a reconnect splice.
            "-c:a", "aac", "-b:a", "256k", "-af", "aresample=async=1000:min_hard_comp=0.1",
            "-avoid_negative_ts", "make_zero",
        });
        if (chaptersPath is not null) args.AddRange(new[] { "-map_chapters", "1" });
        args.Add(outputPath);
        return args;
    }

    /// <summary>A finished file's reported length is broken when it runs well past the footage that went in. The
    /// slack covers ±1 partial segment at each end and container rounding; anything beyond it is a timeline jump.</summary>
    public static bool IsOverLong(int durationS, double contentS)
        => contentS >= 60 && durationS > contentS * 1.2 + 120;
}

/// <summary>ffprobe/ffmpeg calls the finalize planner needs, bounded so a wedged process can never hang a finalize.</summary>
public static class SegmentTools
{
    /// <summary>Per-segment packet timestamps (video + audio) in ONE demux-only ffprobe pass.</summary>
    public static async Task<SegmentProbe> ProbeAsync(string ffprobe, string seg)
    {
        var (ok, outp) = await RunAsync(ffprobe, new[] { "-v", "quiet", "-show_entries", "packet=codec_type,stream_index,pts_time", "-of", "csv=p=0", seg },
            TimeSpan.FromSeconds(30), captureStdout: true);
        return ok ? SegmentProbe.Parse(outp) : SegmentProbe.Unreadable;
    }

    /// <summary>Rewrite [from, to] of a segment (original clock, output-side -ss/-to under -copyts: timestamp-filtered
    /// and seek-free, so it is correct even on a file whose clocks interleave) into `fix/&lt;name&gt;.fix.ts`, stream-copied.
    /// The subfolder keeps rewrites out of the `seg-*.ts` glob, so a re-finalize after a restart never counts a
    /// rewrite as an extra segment. Returns the new file's own video span, or null on any failure.</summary>
    public static async Task<RewriteResult?> RewriteWindowAsync(string ffmpeg, string ffprobe, string seg, double from, double to)
    {
        var inv = CultureInfo.InvariantCulture;
        var fixDir = Path.Combine(Path.GetDirectoryName(seg)!, "fix");
        try { Directory.CreateDirectory(fixDir); } catch { return null; }
        var outPath = Path.Combine(fixDir, Path.GetFileNameWithoutExtension(seg) + ".fix.ts");
        var (ok, _) = await RunAsync(ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-copyts", "-i", seg,
            "-ss", from.ToString("0.###", inv), "-to", to.ToString("0.###", inv),
            "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-f", "mpegts", outPath,
        }, TimeSpan.FromSeconds(30), captureStdout: false);
        if (!ok || !File.Exists(outPath) || new FileInfo(outPath).Length == 0) return null;
        var p = await ProbeAsync(ffprobe, outPath);
        // The rewrite must itself come out on one clock — otherwise it fixed nothing.
        if (!p.Ok || p.Clusters.Count != 1 || p.AudioOffClock(p.Min, p.Max)) return null;
        return new RewriteResult(outPath, p.Min, p.Max);
    }

    /// <summary>Duration (s) of a finished file per ffprobe's container estimate; 0 on failure.</summary>
    public static async Task<int> ProbeDurationAsync(string ffprobe, string path)
    {
        var (ok, outp) = await RunAsync(ffprobe, new[] { "-v", "quiet", "-show_entries", "format=duration", "-of", "default=nk=1:nw=1", path },
            TimeSpan.FromSeconds(20), captureStdout: true);
        return ok && double.TryParse(outp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : 0;
    }

    private static async Task<(bool ok, string stdout)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, bool captureStdout)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(timeout);
            // Drain stderr so a chatty process can't block on a full pipe.
            _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync() is not null) { } } catch { } });
            try
            {
                // The timeout must cover the READ too: a wedged ffprobe never closes stdout.
                var outp = captureStdout ? await p.StandardOutput.ReadToEndAsync(cts.Token) : "";
                if (!captureStdout) _ = Task.Run(async () => { try { while (await p.StandardOutput.ReadLineAsync() is not null) { } } catch { } });
                await p.WaitForExitAsync(cts.Token);
                return (p.ExitCode == 0, outp);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return (false, "");
            }
        }
        catch { return (false, ""); }
    }
}
