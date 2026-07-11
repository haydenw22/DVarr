namespace DVarr.Infrastructure;

/// <summary>
/// One global rule (docs/05): every stored/wire time is a UTC epoch (integer seconds). Wall-clock
/// conversion happens only at display/naming time, through the user's <c>timezone_display</c> setting
/// (an IANA zone id resolved against the OS tz database — tzdata ships in the image). If the id can't
/// be resolved the zone falls back to a FIXED UTC+10 (the original hardcoded-Brisbane behaviour), so a
/// bad setting or a missing tz database can never crash a request. There is no naive datetime anywhere.
/// </summary>
public static class EpochTime
{
    public static readonly TimeSpan BrisbaneOffset = TimeSpan.FromHours(10);

    // Fixed +10 fallback for when the configured IANA id can't be resolved (typo / no tzdata).
    private static readonly TimeZoneInfo FallbackZone = TimeZoneInfo.CreateCustomTimeZone(
        "Australia/Brisbane", BrisbaneOffset, "Australia/Brisbane (fixed +10)", "Australia/Brisbane (fixed +10)");

    private static volatile TimeZoneInfo _displayZone = ResolveZone("Australia/Brisbane") ?? FallbackZone;

    /// <summary>The active display timezone — set from <c>timezone_display</c> at startup and on settings save.</summary>
    public static TimeZoneInfo DisplayZone => _displayZone;

    /// <summary>Resolve an IANA zone id against the OS tz database; null when unknown or unavailable.</summary>
    public static TimeZoneInfo? ResolveZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId.Trim()); }
        catch { return null; }
    }

    /// <summary>Point display conversion at a new zone (an unresolvable id falls back to fixed +10).</summary>
    public static void SetDisplayZone(string? ianaId) => _displayZone = ResolveZone(ianaId) ?? FallbackZone;

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static DateTimeOffset ToUtc(long epochSeconds) => DateTimeOffset.FromUnixTimeSeconds(epochSeconds);

    /// <summary>A UTC epoch as a wall-clock instant in the configured display timezone.</summary>
    public static DateTimeOffset ToDisplay(long epochSeconds) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(epochSeconds), _displayZone);

    /// <summary>Midnight starting the given calendar date IN the display zone, as a UTC epoch — the anchor for
    /// date-only events. A date whose midnight doesn't exist (a DST spring-forward boundary) anchors an hour later.</summary>
    public static long DisplayMidnightUtc(int year, int month, int day)
    {
        var local = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        try { return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _displayZone)).ToUnixTimeSeconds(); }
        catch (ArgumentException)
        {
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), _displayZone)).ToUnixTimeSeconds();
        }
    }

    /// <summary>A floating (zone-less) wall-clock time interpreted in the display zone, as a UTC epoch.</summary>
    public static long DisplayWallClockToUtc(DateTime naive)
    {
        var local = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
        try { return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _displayZone)).ToUnixTimeSeconds(); }
        catch (ArgumentException)
        {
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), _displayZone)).ToUnixTimeSeconds();
        }
    }

    public static long FromUtc(DateTimeOffset instant) => instant.ToUnixTimeSeconds();
}
