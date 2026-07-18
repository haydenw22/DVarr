using System.Text;
using System.Text.Json;

namespace DVarr.Services.Recording;

/// <summary>One live status transition observed during capture (from Recording.LiveMarksJson).</summary>
public sealed record LiveMark(long T, string S, string? P);

/// <summary>
/// Turns the status transitions AutoStopService recorded during capture into an ffmetadata chapters file
/// that finalize embeds into the MKV. Embedded Matroska chapters are the universally-readable form — Plex,
/// Jellyfin, Emby, Kodi and VLC all surface them natively, no sidecar needed. Soccer statuses map to friendly
/// names (Kick-off / Half-time / Second half / Extra time / Penalty shoot-out / Full time); any other sport's
/// statuses (Q1…Q4, OT, innings…) fall through as their own chapter titles, so this generalises for free.
/// </summary>
public static class ChapterMarks
{
    public static List<LiveMark> Parse(string? json)
    {
        var marks = new List<LiveMark>();
        if (string.IsNullOrWhiteSpace(json)) return marks;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                var t = m.TryGetProperty("t", out var tv) && tv.TryGetInt64(out var tl) ? tl : 0;
                var s = m.TryGetProperty("s", out var sv) ? sv.GetString() : null;
                var p = m.TryGetProperty("p", out var pv) ? pv.GetString() : null;
                if (t > 0 && !string.IsNullOrWhiteSpace(s)) marks.Add(new LiveMark(t, s!.Trim(), p));
            }
        }
        catch { /* corrupt marks → no chapters */ }
        return marks;
    }

    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    { "FT", "AET", "AP", "Match Finished", "Finished", "Full Time", "Race Finished" };

    private static readonly HashSet<string> SkipStatuses = new(StringComparer.OrdinalIgnoreCase)
    { "NS", "Not Started", "TBD", "Postponed", "Cancelled" };

    /// <summary>Friendly chapter title for a raw status; null = not chapter-worthy; unmapped = the raw status
    /// itself (correct for sports we haven't special-cased yet).</summary>
    public static string? Title(string status)
    {
        var s = status.Trim();
        if (SkipStatuses.Contains(s)) return null;
        if (TerminalStatuses.Contains(s)) return "Full time";
        return s.ToUpperInvariant() switch
        {
            "1H" or "1ST HALF" or "FIRST HALF" => "Kick-off",
            "HT" or "HALF TIME" or "HALFTIME" => "Half-time",
            "2H" or "2ND HALF" or "SECOND HALF" => "Second half",
            "ET" or "EXTRA TIME" => "Extra time",
            "BREAK" => "Break",
            "P" or "PT" or "PEN" or "PENALTIES" or "PENALTY SHOOTOUT" => "Penalty shoot-out",
            "LIVE" => "In play",
            _ => s,
        };
    }

    /// <summary>
    /// Build the ;FFMETADATA1 chapters document. <paramref name="captureStartUtc"/> anchors wall-clock marks to
    /// file offsets; <paramref name="estimatedDurationS"/> only bounds the LAST chapter's end. Returns null when
    /// no usable chapter emerges (finalize then simply omits the chapters input).
    /// </summary>
    public static string? BuildFfmetadata(IReadOnlyList<LiveMark> marks, long captureStartUtc, int estimatedDurationS)
    {
        if (marks.Count == 0 || captureStartUtc <= 0) return null;

        var chapters = new List<(long StartMs, string Title)>();
        string? lastTitle = null;
        var terminalSeen = false;
        foreach (var m in marks.OrderBy(m => m.T))
        {
            if (terminalSeen) break; // post-final flapping adds nothing after "Full time"
            var title = Title(m.S);
            if (title is null) continue;
            if (string.Equals(title, lastTitle, StringComparison.OrdinalIgnoreCase)) continue;
            var offsetS = m.T - captureStartUtc;
            if (offsetS < 0) offsetS = 0;
            chapters.Add((offsetS * 1000, title));
            lastTitle = title;
            if (title == "Full time") terminalSeen = true;
        }
        if (chapters.Count == 0) return null;

        // Pre-roll gets its own chapter when the first real mark is meaningfully into the file.
        if (chapters[0].StartMs > 90_000) chapters.Insert(0, (0, "Pre-game"));
        else if (chapters[0].StartMs > 0) chapters[0] = (0, chapters[0].Title); // tiny lead-in — snap to 0

        return RenderChapters(chapters, estimatedDurationS);
    }

    /// <summary>
    /// Fallback chapters from the SCHEDULE alone, for when no live status was captured (LiveMarksJson empty — e.g.
    /// TheSportsDB carried no livescore for the fixture). Produces Pre-game / Kick-off (scheduled) / Post-game from
    /// the scheduled kickoff and window-end, relative to the capture start. These are estimates — the "(scheduled)"
    /// label is deliberate — but they still give a one-press skip past the studio pre-roll. Returns null when there
    /// is no bracketing structure worth embedding (no pre-roll AND no post-roll).
    /// </summary>
    public static string? BuildScheduledFfmetadata(long captureStartUtc, long scheduledStartUtc, long scheduledEndUtc, int estimatedDurationS)
    {
        if (captureStartUtc <= 0 || estimatedDurationS <= 0) return null;

        var kickoffS = Math.Max(0, scheduledStartUtc - captureStartUtc);
        var postS = scheduledEndUtc - captureStartUtc;

        var chapters = new List<(long StartMs, string Title)>();
        if (kickoffS > 90) chapters.Add((0, "Pre-game"));            // meaningful pre-roll before kickoff
        chapters.Add((kickoffS * 1000, "Kick-off (scheduled)"));
        if (postS > kickoffS + 60 && postS < (long)estimatedDurationS - 60)
            chapters.Add((postS * 1000, "Post-game"));               // file ran past the scheduled end (post-pad / auto-stop extension)

        // A lone kickoff-at-0 marker isn't worth a chapter track — need at least one bracketing chapter.
        if (chapters.Count < 2) return null;
        return RenderChapters(chapters, estimatedDurationS);
    }

    /// <summary>Render an ordered (offset, title) list as an ;FFMETADATA1 chapters document. Chapter ENDs are
    /// cosmetic (players navigate by starts) — each ends where the next begins, the last at the estimated tail.</summary>
    private static string RenderChapters(List<(long StartMs, string Title)> chapters, int estimatedDurationS)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        for (var i = 0; i < chapters.Count; i++)
        {
            var start = chapters[i].StartMs;
            var end = i + 1 < chapters.Count ? chapters[i + 1].StartMs : Math.Max(start + 60_000, (long)estimatedDurationS * 1000);
            if (end <= start) end = start + 1000;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append("START=").Append(start).Append('\n');
            sb.Append("END=").Append(end).Append('\n');
            sb.Append("title=").Append(EscapeFfmeta(chapters[i].Title)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>ffmetadata escaping: '=', ';', '#', '\' and newline are special in values.</summary>
    private static string EscapeFfmeta(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (c is '\n' or '\r') { sb.Append(' '); continue; }
            if (c is '=' or ';' or '#' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
