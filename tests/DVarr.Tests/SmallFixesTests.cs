using System.Text;
using System.Text.Json;
using DVarr.Api;
using DVarr.Data;
using DVarr.Services.Events;
using DVarr.Services.Ingest;

namespace DVarr.Tests;

public class JellyfinWebhookTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Theory]
    [InlineData("""{"NotificationType":"UserDataSaved","SaveReason":"PlaybackProgress","Played":true}""", true)]
    [InlineData("""{"NotificationType":"UserDataSaved","SaveReason":"TogglePlayed","Played":true}""", false)]
    [InlineData("""{"NotificationType":"UserDataSaved","SaveReason":"PlaybackFinished","Played":true}""", false)]
    [InlineData("""{"NotificationType":"PlaybackStop","PlayedToCompletion":true}""", false)]
    [InlineData("""{"Played":true}""", false)] // a custom template without the type fields still counts
    public void ProgressSavesAreNotWatchedEvents(string payload, bool isProgress)
        => Assert.Equal(isProgress, WebhookEndpoints.IsProgressSave(Json(payload)));

    [Fact]
    public void TwoCopiesOfTheSameGame_AreBothMarked()
    {
        // 20 August: a rescue copy filed beside the original capture made "MLB S2026E1904" match two files, so
        // neither was ever marked watched (and the warning repeated every few seconds).
        var rows = new List<(int, int?, string)> { (10, 77, "Baltimore Orioles vs New York Yankees"), (11, 77, "Baltimore Orioles vs New York Yankees") };
        Assert.Equal(new[] { 10, 11 }, WebhookEndpoints.Disambiguate(rows, "Baltimore Orioles vs New York Yankees"));
    }

    [Fact]
    public void DifferentGamesSharingAKey_AreToldApartByTitle()
    {
        var rows = new List<(int, int?, string)> { (10, 77, "Baltimore Orioles vs New York Yankees"), (11, 78, "Boston Red Sox vs New York Yankees") };
        Assert.Equal(new[] { 11 }, WebhookEndpoints.Disambiguate(rows, "boston red sox vs new york yankees"));
    }

    [Fact]
    public void GenuinelyAmbiguous_IsRefused()
    {
        var rows = new List<(int, int?, string)> { (10, 77, "Game"), (11, 78, "Game") };
        Assert.Null(WebhookEndpoints.Disambiguate(rows, "Game"));
        Assert.Null(WebhookEndpoints.Disambiguate(rows, null));
        // Unlinked files are never assumed to be the same game.
        Assert.Null(WebhookEndpoints.Disambiguate(new List<(int, int?, string)> { (1, null, "A"), (2, null, "B") }, null));
    }
}

public class XtreamResponseTests
{
    [Theory]
    [InlineData("<html><body>Not found</body></html>", true)]
    [InlineData("\r\n  <!DOCTYPE html>", true)]
    [InlineData("\uFEFF<html>", true)]
    [InlineData("[{\"stream_id\":1}]", false)]
    [InlineData("{\"user_info\":{}}", false)]
    [InlineData("", false)]
    public void HtmlIsDetectedBeforeTheJsonParserSeesIt(string body, bool html)
        => Assert.Equal(html, XtreamClient.LooksLikeHtml(Encoding.UTF8.GetBytes(body))); // U+FEFF encodes as the EF BB BF BOM

    [Theory]
    [InlineData("m3u", true)]
    [InlineData(" M3U ", true)]
    [InlineData("xtream", false)]
    [InlineData(null, false)]
    public void M3uSourcesAreRecognised(string? type, bool m3u) => Assert.Equal(m3u, IngestService.IsM3u(type));
}

public class RescueAndScheduleTests
{
    private const long Jul28_1910 = 1_785_280_200;
    private const long Day = 86400;

    private const long Game = 3 * 3600;
    private static RescueSweepService.EventChange Change(EventStatus status, long eventStart, long now, long? snapStart = null)
        => RescueSweepService.ClassifyEventChange(status, eventStart, eventStart + Game,
            Jul28_1910 - 120, Jul28_1910 + Game + 1800,                  // the original capture's padded window
            snapStart ?? Jul28_1910, (snapStart ?? Jul28_1910) + Game, now);

    [Fact]
    public void ARainoutMadeUpTomorrow_IsAMoveIntoTheFuture()
        // 29 July 12:59: the ticket still held 28 July 19:10; the event now says 29 July 19:10.
        => Assert.Equal(RescueSweepService.EventChange.MovedToFuture, Change(EventStatus.Scheduled, Jul28_1910 + Day, Jul28_1910 + Day - 6 * 3600));

    [Fact]
    public void AGameMovedAFewHoursLater_IsAlsoAMoveIntoTheFuture()
        // The review case: a 13:10 capture failed and the game was moved to 19:10 the same day. Both services must
        // agree it is still to come — the hunt stands down AND the scheduler records it (CoversAiring is false).
        => Assert.Equal(RescueSweepService.EventChange.MovedToFuture, Change(EventStatus.Scheduled, Jul28_1910 + 6 * 3600, Jul28_1910 + 3 * 3600));

    [Theory]
    [InlineData(EventStatus.Scheduled, 900, RescueSweepService.EventChange.None)]        // minutes of drift
    [InlineData(EventStatus.Postponed, 0, RescueSweepService.EventChange.Dead)]
    [InlineData(EventStatus.Cancelled, 0, RescueSweepService.EventChange.Dead)]
    [InlineData(EventStatus.Scheduled, -5 * 3600, RescueSweepService.EventChange.MovedInPast)]
    public void EventChanges(EventStatus status, long shift, RescueSweepService.EventChange expected)
        => Assert.Equal(expected, Change(status, Jul28_1910 + shift, Jul28_1910 + Day));

    [Fact]
    public void ARetargetedPastTicket_DoesNotLoop()
        // After a MovedInPast re-target the snapshot matches the event, so the next pass sees no change.
        => Assert.Equal(RescueSweepService.EventChange.None, Change(EventStatus.Scheduled, Jul28_1910 - 5 * 3600, Jul28_1910 + Day, snapStart: Jul28_1910 - 5 * 3600));

    [Fact]
    public void TheSeriesNextGameAiringLive_IsNotAReAir()
    {
        var siblings = new[] { Jul28_1910 + Day - 6 * 3600 }; // tomorrow's 13:10 game against the same team
        Assert.True(RescueSweepService.IsSiblingAiring(Jul28_1910 + Day - 6 * 3600 - 600, Jul28_1910, siblings));
        // A genuine overnight re-air of this game is still fine.
        Assert.False(RescueSweepService.IsSiblingAiring(Jul28_1910 + 8 * 3600, Jul28_1910, siblings));
    }

    [Fact]
    public void ACaptureOfTheOldDate_DoesNotCoverTheMadeUpGame()
    {
        // The capture ran 28 July 19:08–22:40; the event now airs 29 July 19:10–22:10.
        Assert.False(AutoScheduleService.CoversAiring(Jul28_1910 - 120, Jul28_1910 + 12600, Jul28_1910 + Day, Jul28_1910 + Day + Game));
        // Nor does a capture of 13:10 cover the same game moved to 19:10.
        Assert.False(AutoScheduleService.CoversAiring(Jul28_1910 - 120, Jul28_1910 + 12600, Jul28_1910 + 6 * 3600, Jul28_1910 + 6 * 3600 + Game));
        // Half an hour of drift is still the same airing.
        Assert.True(AutoScheduleService.CoversAiring(Jul28_1910 - 120, Jul28_1910 + 12600, Jul28_1910 + 1800, Jul28_1910 + 1800 + Game));
    }
}
