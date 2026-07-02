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
        if (t.Contains("practice 1") || t.Contains("fp1")) return "Practice 1";
        if (t.Contains("practice 2") || t.Contains("fp2")) return "Practice 2";
        if (t.Contains("practice 3") || t.Contains("fp3")) return "Practice 3";
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

    /// <summary>The distinct session kinds present across a set of event titles, in canonical order (drives the picker
    /// so V8 offers only "Race" while F1 offers its full set).</summary>
    public static List<string> KindsPresent(IEnumerable<string?> titles)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in titles) { var k = Classify(t); if (k != null) found.Add(k); }
        return CanonicalKinds.Where(found.Contains).ToList();
    }
}
