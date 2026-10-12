using DVarr.Services.Recording;
using static DVarr.Services.Recording.AutoStopPolicy;

namespace DVarr.Tests;

public class AutoStopPolicyTests
{
    // A 13:10 game scheduled for 3h (ends 16:10), baseball cap 3h → may extend to 19:10.
    private const long Start = 1_785_000_000;
    private const long SchedEnd = Start + 3 * 3600;
    private const long Allowed = SchedEnd + 3 * 3600;

    private static Decision D(StatusClass cls, long now, long recEnd = SchedEnd, bool confirmed = false, long? slot = null,
        bool early = false, long allowed = Allowed)
        => Decide(cls, confirmed, now, recEnd, SchedEnd, Start, allowed, slot, early);

    [Theory]
    [InlineData("IN7", false, StatusClass.InPlay)]      // MLB innings
    [InlineData("Q3", false, StatusClass.InPlay)]
    [InlineData("P2", false, StatusClass.InPlay)]
    [InlineData("2H", false, StatusClass.InPlay)]
    [InlineData("FT", false, StatusClass.Terminal)]
    [InlineData("FT", true, StatusClass.Terminal)]       // terminal wins over livescore presence
    [InlineData("Final/OT", false, StatusClass.Terminal)]
    [InlineData("NS", false, StatusClass.NotStarted)]
    [InlineData("Not Started", false, StatusClass.NotStarted)]
    [InlineData("NS", true, StatusClass.InPlay)]         // in the livescore feed = in play, whatever the status says
    [InlineData("", false, StatusClass.Unknown)]         // motorsport reports no status at all
    [InlineData(null, false, StatusClass.Unknown)]
    [InlineData("INTR", false, StatusClass.Unknown)]     // unmapped (an interruption) keeps failing long
    public void Classify(string? status, bool inLiveScore, StatusClass expected)
        => Assert.Equal(expected, AutoStopPolicy.Classify(status, inLiveScore));

    [Fact]
    public void InPlay_NearTheEnd_ExtendsOneStep()
    {
        var d = D(StatusClass.InPlay, SchedEnd - 60);
        Assert.Equal(ActionKind.Extend, d.Kind);
        Assert.Equal(SchedEnd + 900, d.NewEnd);
    }

    [Fact]
    public void InPlay_FarFromTheEnd_DoesNothingYet()
        => Assert.Equal(ActionKind.None, D(StatusClass.InPlay, SchedEnd - 3600).Kind);

    [Fact]
    public void UnknownStatus_StillFailsLong()
        => Assert.Equal(ActionKind.Extend, D(StatusClass.Unknown, SchedEnd - 60).Kind);

    [Fact]
    public void StaleNotStarted_HoldsAtTheScheduledEnd()
    {
        // 29 July: doubleheader game 1 reported "NS" for its whole broadcast and was extended three hours.
        var d = D(StatusClass.NotStarted, SchedEnd - 60);
        Assert.Equal(ActionKind.None, d.Kind);
        Assert.NotEmpty(d.Why);
    }

    [Fact]
    public void StaleNotStarted_MayExtendToTheBroadcastersGuideSlot()
    {
        var d = D(StatusClass.NotStarted, SchedEnd - 60, slot: SchedEnd + 1800);
        Assert.Equal(ActionKind.Extend, d.Kind);
        Assert.Equal(SchedEnd + 900, d.NewEnd);
        // …but never past it.
        var d2 = D(StatusClass.NotStarted, SchedEnd + 800, recEnd: SchedEnd + 900, slot: SchedEnd + 1800);
        Assert.Equal(SchedEnd + 1800, d2.NewEnd);
    }

    [Fact]
    public void NotStartedThatSuddenlyGoesLiveInThePostPad_StillExtends()
    {
        // A very long pre-game rain delay: "NS" right up to the scheduled end, then play starts during the post-pad.
        var d = D(StatusClass.InPlay, SchedEnd + 1200);
        Assert.Equal(ActionKind.Extend, d.Kind);
        Assert.Equal(SchedEnd + 1200 + 900, d.NewEnd); // stepped from NOW, not from an end already in the past
    }

    [Fact]
    public void UnconfirmedFinal_HoldsInsteadOfExtending()
    {
        // 2 August: "FT FT'" — a first terminal sighting used to count as unknown and buy another 15 minutes.
        Assert.Equal(ActionKind.None, D(StatusClass.Terminal, SchedEnd - 60, confirmed: false).Kind);
    }

    [Fact]
    public void ConfirmedFinal_TrimsAnUnusedExtensionBack()
    {
        var d = D(StatusClass.Terminal, SchedEnd + 300, recEnd: SchedEnd + 900, confirmed: true);
        Assert.Equal(ActionKind.Clamp, d.Kind);
        Assert.Equal(SchedEnd + 300, d.NewEnd);
    }

    [Fact]
    public void ConfirmedFinal_NeverCutsTheScheduledWindowByDefault()
        => Assert.Equal(ActionKind.None, D(StatusClass.Terminal, SchedEnd - 1800, confirmed: true).Kind);

    [Fact]
    public void ConfirmedFinal_WithStopEarly_EndsNow()
    {
        var d = D(StatusClass.Terminal, SchedEnd - 1800, confirmed: true, early: true);
        Assert.Equal(ActionKind.Clamp, d.Kind);
        Assert.Equal(SchedEnd - 1800, d.NewEnd);
    }

    [Fact]
    public void StopEarly_IgnoresAFinalBeforeTheGameCouldHaveBeenPlayed()
        => Assert.Equal(ActionKind.None, D(StatusClass.Terminal, Start + 600, confirmed: true, early: true).Kind);

    [Fact]
    public void ExtensionStopsAtTheNextBooking()
    {
        var d = D(StatusClass.InPlay, SchedEnd - 60, allowed: SchedEnd);
        Assert.Equal(ActionKind.CapReached, d.Kind);
    }
}
