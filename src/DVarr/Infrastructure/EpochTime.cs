namespace DVarr.Infrastructure;

/// <summary>
/// One global rule (docs/05): every stored/wire time is a UTC epoch (integer seconds).
/// Brisbane is a FIXED UTC+10 (no DST), applied only at display — so we never depend on
/// OS timezone data and InvariantGlobalization stays safe. This is the structural fix
/// for the legacy-DVR +10h double-conversion bug (#5): there is no naive datetime anywhere.
/// </summary>
public static class EpochTime
{
    public static readonly TimeSpan BrisbaneOffset = TimeSpan.FromHours(10);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static DateTimeOffset ToUtc(long epochSeconds) => DateTimeOffset.FromUnixTimeSeconds(epochSeconds);

    public static DateTimeOffset ToBrisbane(long epochSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(BrisbaneOffset);

    public static long FromUtc(DateTimeOffset instant) => instant.ToUnixTimeSeconds();
}
