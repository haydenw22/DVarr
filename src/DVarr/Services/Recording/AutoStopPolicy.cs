using DVarr.Services.Events;

namespace DVarr.Services.Recording;

/// <summary>
/// The smart auto-stop decision for one live recording, separated from the polling/DB plumbing in
/// <see cref="AutoStopService"/> so it can be table-tested. Fail LONG on a genuinely live or silent feed, but never
/// let a feed that is plainly stale (or a final that hasn't been confirmed yet) keep stretching a recording.
/// </summary>
public static class AutoStopPolicy
{
    public enum StatusClass { Terminal, InPlay, NotStarted, Unknown }

    public enum ActionKind { None, Extend, Clamp, CapReached }

    public readonly record struct Decision(ActionKind Kind, long NewEnd = 0, string Why = "");

    /// <summary>Guide statuses that mean the event is OVER (case-insensitive, trimmed). Soccer: FT / AET / AP (after
    /// penalties); generic feeds also emit the spelled-out forms; AW/WO = awarded/walk-over. "PEN"/"P"/"PT" are
    /// deliberately NOT here — feeds are inconsistent about whether they mean a shoot-out IN PROGRESS or finished, and
    /// clamping mid-shootout is the one mistake this service exists to prevent.</summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "FT", "AET", "AP", "Match Finished", "Finished", "Full Time", "Race Finished",
        "Cancelled", "Postponed", "Abandoned", "AW", "WO",
        // Non-soccer terminals (baseball / basketball / NFL / hockey): "Final" (also "Final/OT", "Final/SO" — caught
        // by the prefix test in Classify) or a spelled-out finish.
        "Final", "Game Finished", "Ended", "Complete", "Completed", "After Over Time", "AOT",
    };

    /// <summary>Guide statuses that mean the event is IN PLAY.</summary>
    private static readonly HashSet<string> InPlayStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1H", "2H", "HT", "ET", "BREAK", "P", "PT", "PEN", "Live", "OT", "BT",
        "1st Half", "2nd Half", "First Half", "Second Half", "Half Time", "Halftime",
        "Extra Time", "Penalties", "Penalty Shootout",
    };

    /// <summary>Statuses that explicitly say the event has NOT begun. Distinct from Unknown (an empty or unmapped
    /// status — motorsport reports none at all — which must keep failing long): long after the scheduled start,
    /// "not started" is a feed that has stopped updating, not information about the game.</summary>
    private static readonly HashSet<string> NotStartedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "NS", "Not Started", "TBD", "Time To Be Defined", "Scheduled",
    };

    /// <summary>How long after the scheduled start a "not started" status stops being believable.</summary>
    public const int StaleNotStartedAfterS = 1800;

    /// <summary>TERMINAL / IN-PLAY / NOT-STARTED / UNKNOWN. Terminal wins over livescore presence (a feed can briefly
    /// keep an FT row); livescore presence or a period code (innings IN1.., quarters Q1.., hockey periods P1..) means
    /// in play even when the status string itself is unmapped.</summary>
    public static StatusClass Classify(string? status, bool inLiveScore)
    {
        var s = status?.Trim();
        if (!string.IsNullOrEmpty(s) && (TerminalStatuses.Contains(s) || s.StartsWith("Final", StringComparison.OrdinalIgnoreCase)))
            return StatusClass.Terminal;
        if (inLiveScore) return StatusClass.InPlay;
        if (string.IsNullOrEmpty(s)) return StatusClass.Unknown;
        if (InPlayStatuses.Contains(s) || IsPeriodCode(s)) return StatusClass.InPlay;
        if (NotStartedStatuses.Contains(s)) return StatusClass.NotStarted;
        return StatusClass.Unknown;
    }

    private static bool IsPeriodCode(string s)
    {
        foreach (var prefix in new[] { "IN", "Q", "P" })
            if (s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && s[prefix.Length..].All(char.IsDigit))
                return true;
        return false;
    }

    /// <summary>
    /// Decide what to do with a live recording's end this tick.
    /// <list type="bullet">
    /// <item><b>Terminal, confirmed</b>: trim an unused EXTENSION back to now (never below the scheduled end) — or,
    /// with <paramref name="stopEarlyOnFinal"/>, end at now even inside the scheduled window. Post-pad still runs.</item>
    /// <item><b>Terminal, not yet confirmed</b>: HOLD — neither extend nor clamp. (It used to count as "unknown" and
    /// extend, so every game's final whistle bought an extra 15 minutes.)</item>
    /// <item><b>In play, or Unknown</b>: extend in 15-minute steps up to the cap / next booking — fail long.</item>
    /// <item><b>Not started, but long after the scheduled start</b>: the feed is stale (29 July: a doubleheader game
    /// reported "NS" for its whole broadcast and was extended for three hours). Extend only as far as the guide slot
    /// the broadcaster scheduled for it, if that is known; otherwise hold at the scheduled end.</item>
    /// </list>
    /// </summary>
    public static Decision Decide(StatusClass cls, bool terminalConfirmed, long now, long recEnd, long scheduledEnd,
        long eventStart, long allowedEnd, long? guideSlotEnd, bool stopEarlyOnFinal, int decideAheadS = 120, int stepS = 900)
    {
        if (cls == StatusClass.Terminal)
        {
            if (!terminalConfirmed) return new Decision(ActionKind.None, Why: "final not yet confirmed — holding");
            if (now >= recEnd) return new Decision(ActionKind.None);
            // A "final" before the game could plausibly have been played is a feed glitch, not a result.
            if (stopEarlyOnFinal && now >= eventStart + StaleNotStartedAfterS)
                return new Decision(ActionKind.Clamp, now, "finished per guide");
            var clampTo = Math.Max(now, scheduledEnd);
            return clampTo < recEnd ? new Decision(ActionKind.Clamp, clampTo, "finished per guide") : new Decision(ActionKind.None);
        }

        // Every non-terminal branch only ever GROWS the window, and only close to the current end.
        if (now < recEnd - decideAheadS) return new Decision(ActionKind.None);

        var limit = allowedEnd;
        if (cls == StatusClass.NotStarted && now >= eventStart + StaleNotStartedAfterS)
        {
            if (guideSlotEnd is not { } slot || slot <= recEnd)
                return new Decision(ActionKind.None, Why: "status still says not started — feed looks stale; holding at the scheduled end");
            limit = Math.Min(limit, slot);
        }

        // Step from whichever is later — the current end or now — so a capture deep in its post-pad gets a real
        // extension instead of an end that is already in the past.
        var newEnd = Math.Min(Math.Max(recEnd, now) + stepS, limit);
        return newEnd <= recEnd ? new Decision(ActionKind.CapReached) : new Decision(ActionKind.Extend, newEnd);
    }
}
