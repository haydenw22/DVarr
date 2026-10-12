using System.Diagnostics;
using DVarr.Services.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace DVarr.Tests;

/// <summary>Runs only where ffmpeg and ffprobe are on PATH (CI installs them; the container image ships them).</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!Ffmpeg.Available) Skip = "ffmpeg/ffprobe not on PATH";
    }
}

internal static class Ffmpeg
{
    public static readonly bool Available = Runs("ffmpeg") && Runs("ffprobe");

    private static bool Runs(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg {string.Join(' ', args)} failed: {err}");
    }
}

/// <summary>
/// End-to-end reproduction of the broken-length recordings, through the production finalize path (probe → plan →
/// rewrite → the exact concat command finalize runs). Segments are synthesised to match the real failure shapes:
/// 8-second MPEG-TS files on an old clock (~82,276s) followed by files on a restarted clock (~0s), with half a second
/// of audio straggling across the restart.
/// </summary>
public class FinalizeIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvarr-finalize-" + Guid.NewGuid().ToString("N")[..8]);

    public FinalizeIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>old1 old2 (old clock), new1 new2 (restarted clock), plus the two straggler variants.</summary>
    private void BuildSegments()
    {
        Ffmpeg.Run(_dir, "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=8", "-f", "lavfi", "-i", "sine=frequency=440:duration=8",
            "-c:v", "libx264", "-g", "25", "-c:a", "aac", "-streamid", "0:256", "-streamid", "1:257", "-f", "mpegts", "base.ts");
        void Mk(string name, string offset) =>
            Ffmpeg.Run(_dir, "-i", "base.ts", "-map", "0", "-c", "copy", "-output_ts_offset", offset, "-streamid", "0:256", "-streamid", "1:257", "-f", "mpegts", name);
        Mk("seg-01-old1.ts", "82275"); Mk("seg-02-old2.ts", "82283"); Mk("seg-03-new1.ts", "0"); Mk("seg-04-new2.ts", "8");
        // Half a second of audio on the NEW clock, appended to the last old-clock segment (same PID) — the 30 July shape.
        Ffmpeg.Run(_dir, "-f", "lavfi", "-i", "sine=frequency=440:duration=0.5", "-c:a", "aac", "-output_ts_offset", "0", "-streamid", "0:257", "-f", "mpegts", "tail.ts");
        File.WriteAllBytes(P("straggler.ts"), File.ReadAllBytes(P("seg-02-old2.ts")).Concat(File.ReadAllBytes(P("tail.ts"))).ToArray());
        // Half a second of audio on the OLD clock, leading a new-clock segment — the 29 July download shape.
        Ffmpeg.Run(_dir, "-f", "lavfi", "-i", "sine=frequency=440:duration=0.5", "-c:a", "aac", "-output_ts_offset", "82291.4", "-streamid", "0:257", "-f", "mpegts", "lead.ts");
        File.WriteAllBytes(P("leader.ts"), File.ReadAllBytes(P("lead.ts")).Concat(File.ReadAllBytes(P("seg-03-new1.ts"))).ToArray());
    }

    private async Task<int> FinalizeAsync(string name, IReadOnlyList<string> segs, bool dedupe, bool planned = true)
    {
        var listPath = P(name + ".ffconcat");
        if (planned)
        {
            var probes = new List<SegmentProbe>();
            foreach (var s in segs) probes.Add(await SegmentTools.ProbeAsync("ffprobe", s));
            var plan = await ConcatPlanner.BuildAsync(segs, probes, dedupe,
                (seg, from, to) => SegmentTools.RewriteWindowAsync("ffmpeg", "ffprobe", seg, from, to), NullLogger.Instance);
            await File.WriteAllLinesAsync(listPath, plan.Lines);
        }
        else
            await File.WriteAllLinesAsync(listPath, new[] { "ffconcat version 1.0" }.Concat(segs.Select(ConcatPlanner.FileLine)));
        var output = P(name + ".mkv");
        var args = ConcatPlanner.FinalizeArgs(listPath, null, output);
        Ffmpeg.Run(_dir, args.Skip(4).ToArray()); // Run adds its own -hide_banner/-loglevel/-y
        return await SegmentTools.ProbeDurationAsync("ffprobe", output);
    }

    [FfmpegFact]
    public async Task AStragglingAudioPacket_NoLongerInflatesTheFile()
    {
        BuildSegments();
        var segs = new[] { P("seg-01-old1.ts"), P("straggler.ts"), P("seg-03-new1.ts"), P("seg-04-new2.ts") };

        // The bug, reproduced: the plain list the old code effectively produced runs to hours.
        Assert.True(await FinalizeAsync("before", segs, dedupe: true, planned: false) > 10_000);

        // Live capture and catch-up download alike now come out at the 32s of footage that went in.
        Assert.InRange(await FinalizeAsync("live", segs, dedupe: true), 30, 34);
        Assert.InRange(await FinalizeAsync("catchup", segs, dedupe: false), 30, 34);
    }

    [FfmpegFact]
    public async Task AudioLeadingOnTheOldClock_NoLongerInflatesTheFile()
    {
        BuildSegments();
        var segs = new[] { P("seg-01-old1.ts"), P("seg-02-old2.ts"), P("leader.ts"), P("seg-04-new2.ts") };
        Assert.True(await FinalizeAsync("before", segs, dedupe: false, planned: false) > 10_000);
        Assert.InRange(await FinalizeAsync("catchup", segs, dedupe: false), 30, 34);
        Assert.InRange(await FinalizeAsync("live", segs, dedupe: true), 30, 34);
    }

    [FfmpegFact]
    public async Task ACleanRestartedClock_IsUnchanged()
    {
        BuildSegments();
        var segs = new[] { P("seg-01-old1.ts"), P("seg-02-old2.ts"), P("seg-03-new1.ts"), P("seg-04-new2.ts") };
        Assert.InRange(await FinalizeAsync("clean", segs, dedupe: true), 30, 34);
        // Nothing was rewritten for a healthy timeline.
        Assert.False(Directory.Exists(P("fix")));
    }

    [FfmpegFact]
    public async Task RewrittenSegmentsLiveOutsideTheSegmentGlob()
    {
        BuildSegments();
        var rw = await SegmentTools.RewriteWindowAsync("ffmpeg", "ffprobe", P("straggler.ts"), 82281.4, 82293.4);
        Assert.NotNull(rw);
        Assert.Equal(Path.Combine(_dir, "fix"), Path.GetDirectoryName(rw!.Path));
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir, "seg-*.ts"), f => f.Contains(".fix."));
        var p = await SegmentTools.ProbeAsync("ffprobe", rw.Path);
        Assert.False(p.AudioOffClock(p.Min, p.Max));
        Assert.InRange(p.Max - p.Min, 7.5, 8.5);
    }
}
