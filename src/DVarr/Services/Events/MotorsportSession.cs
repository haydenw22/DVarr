namespace DVarr.Services.Events;

/// <summary>
/// Classifies a motorsport event's title into a canonical session kind. Motorsport sessions are separate Event rows
/// whose session lives in the title ("Australian Grand Prix Practice 1", "… Qualifying", "Australian Grand Prix" = race;
/// V8 = "… Race N"). TheSportsDB exposes no session field, so this is the single source of truth — reused by the
/// scheduler's session filter, the calendar follow filter, the League modal's session picker, and per-session duration
/// resolution. Pure + deterministic, so it works on already-ingested events without a re-sync or a stored column.
/// </summary>
public static class MotorsportSession
{
    /// <summary>Canonical session kinds, most-specific first (also the display order in the picker).</summary>
    public static readonly IReadOnlyList<string> CanonicalKinds = new[]
    {
        "Practice 1", "Practice 2", "Practice 3", "Sprint Qualifying", "Sprint", "Qualifying", "Race", "Testing",
    };

    /// <summary>Whether a league's sport is motorsport — the ONLY place session-follow / per-session logic may apply.
    /// Any other sport's titles all classify as "Race" (the fallback), so applying a session filter to a team-sport
    /// league would silently drop every match; every consumer must gate on this.</summary>
    public static bool IsMotorsport(string? sport)
        => sport != null && sport.Contains("motorsport", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort session kind for a motorsport event title. A bare event/Grand-Prix name (no session word)
    /// is the main Race. Returns null only for an empty title. Order matters: longer/more-specific matches win.</summary>
    public static string? Classify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title.ToLowerInvariant();
        if (t.Contains("testing")) return "Testing";
        // Whole-token match: "practice 1"/"fp1" must NOT also swallow "practice 10"/"fp10" — a trailing digit means a
        // different session, so require the number to end on a word boundary (\b fails digit-to-digit).
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\bpractice 1\b|\bfp1\b")) return "Practice 1";
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\bpractice 2\b|\bfp2\b")) return "Practice 2";
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\bpractice 3\b|\bfp3\b")) return "Practice 3";
        // "Sprint Qualifying" / "Sprint Shootout" must beat plain "Sprint" and "Qualifying".
        if (t.Contains("sprint qualifying") || t.Contains("sprint shootout")) return "Sprint Qualifying";
        // A "race"/"Race N" token wins over a "Sprint" that's part of the MEETING name (V8 "… SuperSprint Race 4" is a
        // Race, not a Sprint session) — check it as a whole word before the bare "sprint" contains-match.
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\brace\b")) return "Race";
        if (t.Contains("sprint")) return "Sprint";
        if (t.Contains("qualifying")) return "Qualifying";
        if (t.Contains("practice")) return "Practice 1"; // an unnumbered practice → fold into P1
        return "Race"; // bare GP name / anything else = the race
    }

    /// <summary>Built-in per-session recording length (seconds) for a motorsport session kind, or null for an
    /// unknown/null kind. Philosophy: keep these TIGHT. A support session — Practice, Sprint, a Sprint Qualifying /
    /// Sprint Shootout, or the main Qualifying — is really ~1h of track action, so it gets 3600s and stops blocking a
    /// full 3h credential window pointlessly (an F1 Sprint/Quali/Practice is an hour). We can be aggressive because two
    /// safety nets sit downstream: (a) every recording still gets the +30min post-pad, and (b) AutoStopService extends a
    /// LIVE recording past its scheduled end while the guide still shows the session in play — so a session that runs
    /// long is never truncated. Only the Race (10800s) and Testing (10800s) stay long: races keep the full 3h window per
    /// the "keep full race endings" house rule (never trim a race ending), and a testing day genuinely runs for hours.
    /// This is a DEFAULT tier only — an explicit user per-session or per-league override always wins over it.</summary>
    public static int? DefaultDurationS(string? kind) => kind switch
    {
        "Practice 1" or "Practice 2" or "Practice 3" => 3600,
        "Sprint Qualifying" => 3600,
        "Sprint" => 3600,
        "Qualifying" => 3600,
        "Race" => 10800,
        "Testing" => 10800,
        _ => null,
    };

    /// <summary>The distinct session kinds present across a set of event titles, in canonical order (drives the picker
    /// so V8 offers only "Race" while F1 offers its full set).</summary>
    public static List<string> KindsPresent(IEnumerable<string?> titles)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in titles) { var k = Classify(t); if (k != null) found.Add(k); }
        return CanonicalKinds.Where(found.Contains).ToList();
    }
}
