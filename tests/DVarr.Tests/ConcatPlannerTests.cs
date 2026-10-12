using DVarr.Services.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace DVarr.Tests;

/// <summary>
/// The finalize planner's decisions against fake segment timelines. The shapes come from real reports: a segment whose
/// last half-second of audio sat on a restarted (wrapped) clock turned a 3.5h game into a "7h" file that stopped at
/// 2:30 (30 July), and audio leading in on the old clock produced the "22h 46m" download that wouldn't play (29 July).
/// </summary>
public class ConcatPlannerTests
{
    private const double Wrap = 95443.717688; // 2^33 / 90 kHz — where a restarted clock lands after mpegts unwrapping

    /// <summary>A healthy segment: video [vmin, vmax] and audio on the same clock.</summary>
    private static SegmentProbe Seg(double vmin, double vmax) => new(true, vmin, vmax,
        new[] { new PtsCluster(vmin, vmax, 200) }, 0, new[] { new PtsCluster(vmin - 0.02, vmax + 0.03, 375) });

    /// <summary>A segment whose video is clean but whose audio ALSO has a cluster on another clock.</summary>
    private static SegmentProbe SegWithStrayAudio(double vmin, double vmax, double strayMin, double strayMax) => new(true, vmin, vmax,
        new[] { new PtsCluster(vmin, vmax, 200) }, 0,
        new[] { new PtsCluster(vmin - 0.02, vmax + 0.03, 360), new PtsCluster(strayMin, strayMax, 20) }.OrderBy(c => c.Min).ToArray());

    /// <summary>A segment whose VIDEO jumps clocks mid-file: two clusters, the file opening on <paramref name="head"/>.</summary>
    private static SegmentProbe Jump(PtsCluster a, PtsCluster b, int head) => new(true, Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max),
        new[] { a, b }.OrderBy(c => c.Min).ToArray(), head, Array.Empty<PtsCluster>());

    private sealed class FakeRewriter
    {
        public List<(string Seg, double From, double To)> Calls { get; } = new();
        public bool Fail { get; init; }
        public double RewrittenVMin { get; init; } = 3.4;
        public Task<RewriteResult?> Rewrite(string seg, double from, double to)
        {
            Calls.Add((seg, from, to));
            return Task.FromResult<RewriteResult?>(Fail ? null : new RewriteResult(seg + ".fix.ts", RewrittenVMin, RewrittenVMin + (to - from - 4)));
        }
    }

    private static List<string> Names(int n) => Enumerable.Range(1, n).Select(i => $"/seg/seg-{i:D3}.ts").ToList();

    private static Task<ConcatPlan> Plan(IReadOnlyList<SegmentProbe> probes, bool dedupe, FakeRewriter rw)
        => ConcatPlanner.BuildAsync(Names(probes.Count), probes, dedupe, rw.Rewrite, NullLogger.Instance);

    [Fact]
    public async Task CleanTimeline_KeepsEverySegment_WithoutRewrites()
    {
        var rw = new FakeRewriter();
        var plan = await Plan(new[] { Seg(0, 8), Seg(8, 16), Seg(16, 24) }, dedupe: true, rw);
        Assert.Equal(3, plan.Kept);
        Assert.Empty(rw.Calls);
        Assert.Equal(new[] { "ffconcat version 1.0", "file '/seg/seg-001.ts'", "file '/seg/seg-002.ts'", "file '/seg/seg-003.ts'" }, plan.Lines);
        Assert.InRange(plan.ContentS, 23.9, 24.1);
    }

    [Fact]
    public async Task StragglerAudioOnARestartedClock_IsRewrittenToTheVideoWindow()
    {
        // 30 July: the clock restart landed 2h32m in; the segment before it carried 0.5s of audio on the NEW clock,
        // which the demuxer reads as ~Wrap seconds — so the file "lasted" 13,161s.
        var rw = new FakeRewriter();
        var plan = await Plan(new[] { Seg(82276.4, 82284.4), SegWithStrayAudio(82284.4, 82292.4, Wrap + 1.9, Wrap + 2.4), Seg(1.5, 9.4) }, dedupe: true, rw);
        var call = Assert.Single(rw.Calls);
        Assert.Equal("/seg/seg-002.ts", call.Seg);
        Assert.Equal(82282.4, call.From, 3);
        Assert.Equal(82294.4, call.To, 3);
        Assert.Contains("file '/seg/seg-002.ts.fix.ts'", plan.Lines);
        Assert.Equal(1, plan.CrossStreamFixed);
        Assert.Equal(3, plan.Kept);
    }

    [Fact]
    public async Task AudioLeadingOnTheOldClock_IsRewritten_AndTheWindowNeverGoesNegative()
    {
        var rw = new FakeRewriter();
        var plan = await Plan(new[] { Seg(82284.4, 82292.4), SegWithStrayAudio(1.48, 9.44, 82292.4, 82292.9), Seg(9.44, 17.4) }, dedupe: false, rw);
        var call = Assert.Single(rw.Calls);
        Assert.Equal(0, call.From);           // vmin - 2 clamped at zero
        Assert.Equal(11.44, call.To, 3);
        Assert.Equal(3, plan.Kept);
    }

    [Fact]
    public async Task WhenARewriteFails_TheOriginalSegmentIsStillKept()
    {
        var rw = new FakeRewriter { Fail = true };
        var plan = await Plan(new[] { Seg(0, 8), SegWithStrayAudio(8, 16, Wrap, Wrap + 0.5) }, dedupe: true, rw);
        Assert.Single(rw.Calls);
        Assert.Contains("file '/seg/seg-002.ts'", plan.Lines);
        Assert.Equal(2, plan.Kept);
        Assert.Equal(0, plan.CrossStreamFixed);
    }

    [Fact]
    public async Task CatchupPull_KeepsChunksThatRestartTheirClock()
    {
        // Each archive chunk restarts near zero by design; none of it is a re-served duplicate.
        var probes = new[] { Seg(0, 8), Seg(8, 16), Seg(0, 8), Seg(8, 16) };
        var catchup = await Plan(probes, dedupe: false, new FakeRewriter());
        Assert.Equal(4, catchup.Kept);
        Assert.Equal(0, catchup.Dropped);

        // The same timeline on a LIVE capture reads as re-served content (the reason catch-up skips de-overlap).
        var live = await Plan(probes, dedupe: true, new FakeRewriter());
        Assert.True(live.Dropped > 0);
    }

    [Fact]
    public async Task LiveCapture_DropsAReservedDuplicate_AndTrimsAPartialOverlap()
    {
        var plan = await Plan(new[] { Seg(0, 8), Seg(8, 16), Seg(10, 16), Seg(12, 20) }, dedupe: true, new FakeRewriter());
        Assert.Equal(1, plan.Dropped);       // [10..16] sits wholly inside the timeline
        Assert.Equal(1, plan.Trimmed);       // [12..20] overlaps it by four seconds
        Assert.Contains("inpoint 16", plan.Lines);
        Assert.Equal(3, plan.Kept);
    }

    [Fact]
    public async Task PartialOverlapOnARewrittenSegment_TranslatesTheInpointOntoTheRewrittenClock()
    {
        // The rewrite re-zeros the file's clock (original 12 → 3.5 here), so an in-point computed on the original
        // clock (16) must move with it: 16 - 12 + 3.5 = 7.5.
        var rw = new FakeRewriter { RewrittenVMin = 3.5 };
        var plan = await Plan(new[] { Seg(0, 8), Seg(8, 16), SegWithStrayAudio(12, 20, Wrap, Wrap + 0.4) }, dedupe: true, rw);
        Assert.Equal(1, plan.Trimmed);
        Assert.Contains("file '/seg/seg-003.ts.fix.ts'", plan.Lines);
        Assert.Contains("inpoint 7.5", plan.Lines);
    }

    [Fact]
    public async Task ARestartedClock_IsKeptAndRebases()
    {
        var plan = await Plan(new[] { Seg(1000, 1008), Seg(1008, 1016), Seg(0, 8), Seg(8, 16) }, dedupe: true, new FakeRewriter());
        Assert.Equal(1, plan.Rebased);
        Assert.Equal(4, plan.Kept);
        Assert.Equal(0, plan.Dropped);
    }

    [Fact]
    public async Task AVideoClockJump_KeepsTheClusterThatContinuesTheTimeline()
    {
        var rw = new FakeRewriter();
        var jump = Jump(new PtsCluster(82292.4, 82296, 100), new PtsCluster(Wrap, Wrap + 4, 120), head: 0);
        var plan = await Plan(new[] { Seg(82284.4, 82292.4), jump, Seg(82296, 82304) }, dedupe: true, rw);
        Assert.Equal(1, plan.Salvaged);
        var call = Assert.Single(rw.Calls);
        Assert.Equal(82290.4, call.From, 3);
        Assert.Equal(82298, call.To, 3);
        // The next segment continues the kept clock — no overlap, no rebase.
        Assert.Equal(0, plan.Rebased);
        Assert.Equal(3, plan.Kept);
    }

    [Fact]
    public async Task ConsecutiveClockJumps_KeepFollowingTheSameClock()
    {
        // A flapping feed: the timeline must be tracked on the ORIGINAL clock after a rewrite, or the second
        // segment's "continue the timeline" preference is lost and the planner flips to the other clock.
        var rw = new FakeRewriter();
        var j1 = Jump(new PtsCluster(82292, 82294, 50), new PtsCluster(Wrap, Wrap + 6, 150), head: 0);
        // j2's other clock covers MORE time, so only the continuing-timeline preference keeps the planner on 82xxx.
        var j2 = Jump(new PtsCluster(82294, 82300, 50), new PtsCluster(Wrap + 6, Wrap + 14, 150), head: 0);
        await Plan(new[] { Seg(82284, 82292), j1, j2 }, dedupe: true, rw);
        Assert.Equal(2, rw.Calls.Count);
        Assert.All(rw.Calls, c => Assert.InRange(c.From, 82280, 82300));
    }

    [Fact]
    public async Task AJumpSegmentThatCantBeRewritten_FallsBackToAnOutpointOnItsOpeningClock()
    {
        var rw = new FakeRewriter { Fail = true };
        var jump = Jump(new PtsCluster(8, 12, 100), new PtsCluster(Wrap, Wrap + 4, 100), head: 0);
        var plan = await Plan(new[] { Seg(0, 8), jump }, dedupe: true, rw);
        Assert.Equal(1, plan.Salvaged);
        Assert.Contains("file '/seg/seg-002.ts'", plan.Lines);
        Assert.Contains("outpoint 12.2", plan.Lines);
    }

    [Fact]
    public async Task AJumpSegmentWhoseKeptClockIsNotTheOpeningOne_IsDroppedWhenTheRewriteFails()
    {
        var rw = new FakeRewriter { Fail = true };
        var jump = Jump(new PtsCluster(8, 12, 100), new PtsCluster(Wrap, Wrap + 4, 100), head: 1);
        var plan = await Plan(new[] { Seg(0, 8), jump, Seg(12, 20) }, dedupe: true, rw);
        Assert.Equal(1, plan.JumpDropped);
        Assert.Equal(2, plan.Kept);
    }

    [Fact]
    public async Task UnreadableSegments_AreKeptVerbatim()
    {
        var plan = await Plan(new[] { Seg(0, 8), SegmentProbe.Unreadable, Seg(16, 24) }, dedupe: true, new FakeRewriter());
        Assert.Equal(3, plan.Kept);
    }

    [Theory]
    [InlineData(25876, 12600, true)]   // 30 July: a ~3.5h capture that reported 7h
    [InlineData(81948, 12120, true)]   // 29 July: the "22h 46m" download
    [InlineData(12650, 12600, false)]  // container rounding
    [InlineData(12600 * 1.15, 12600, false)]
    [InlineData(400, 30, false)]       // too short to judge
    public void OverLongGuard(double durationS, double contentS, bool flagged)
        => Assert.Equal(flagged, ConcatPlanner.IsOverLong((int)durationS, contentS));
}

public class SegmentProbeTests
{
    [Fact]
    public void Parse_SplitsVideoAndAudio_AndToleratesSideDataCommas()
    {
        var p = SegmentProbe.Parse("video,0,82284.423222,\nvideo,0,82284.503222\naudio,1,82284.400000,\naudio,1,95445.628533,\nvideo,0,82292.343222,\n");
        Assert.True(p.Ok);
        Assert.Equal(82284.423222, p.Min, 5);
        Assert.Equal(82292.343222, p.Max, 5);
        Assert.Single(p.Clusters);
        Assert.Equal(2, p.AudioClusters.Count);
        Assert.True(p.AudioOffClock(p.Min, p.Max));
    }

    [Fact]
    public void Parse_IdentifiesVideoByType_EvenWhenAudioWasDiscoveredFirst()
    {
        // A leading audio packet makes audio stream 0 and video stream 1.
        var p = SegmentProbe.Parse("audio,0,82293.776778,\naudio,0,82293.8,\nvideo,1,1.48,\nvideo,1,9.44,\naudio,0,1.45,\naudio,0,9.47,\n");
        Assert.True(p.Ok);
        Assert.Equal(1.48, p.Min, 5);
        Assert.Equal(9.44, p.Max, 5);
        Assert.True(p.AudioOffClock(p.Min, p.Max));
    }

    [Fact]
    public void Parse_ClustersAVideoClockJump_AndFindsTheOpeningCluster()
    {
        var p = SegmentProbe.Parse("video,0,95443.8\nvideo,0,95444.6\nvideo,0,12.0\nvideo,0,12.8\n");
        Assert.Equal(2, p.Clusters.Count);
        Assert.Equal(1, p.HeadCluster); // sorted: [12..12.8], [95443.8..95444.6]; the file opens on the latter
    }

    [Fact]
    public void Parse_HealthyAudioIsOnClock()
    {
        var p = SegmentProbe.Parse("video,0,8.0\nvideo,0,15.96\naudio,1,7.98\naudio,1,16.01\n");
        Assert.False(p.AudioOffClock(p.Min, p.Max));
    }

    [Theory]
    [InlineData("")]
    [InlineData("audio,0,1.0\naudio,0,2.0\n")]
    [InlineData("video,0,N/A\n")]
    public void Parse_WithoutVideoIsUnreadable(string csv) => Assert.False(SegmentProbe.Parse(csv).Ok);
}
