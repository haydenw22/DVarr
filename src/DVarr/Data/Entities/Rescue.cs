namespace DVarr.Data.Entities;

/// <summary>
/// A second-chance "rescue ticket": opened when a monitored-league game ends with no playable copy (a failed
/// finalize, a missed window, or a conflict that never got a slot). A background sweep (RescueSweepService) then
/// hunts the EPG on the league's mapped channels for a re-air airing after the game, and schedules it as a low
/// (Opportunistic) priority replay that never preempts a live game. Closed once any good copy lands; abandoned
/// after <see cref="ExpiresUtc"/>. Provenance ids are loose snapshots (no FKs) so a sync/delete can't cascade a
/// ticket away mid-hunt.
/// </summary>
public class RescueTicket
{
    public int Id { get; set; }

    /// <summary>The original failed recording that opened this ticket.</summary>
    public int RecordingId { get; set; }
    public int EventId { get; set; }
    public int LeagueId { get; set; }

    /// <summary>Display snapshot (survives deletion of the event/recording rows).</summary>
    public string Title { get; set; } = "";
    /// <summary>Text matched against EPG programme titles (the event title).</summary>
    public string MatchQuery { get; set; } = "";
    /// <summary>The original game window (epoch s): a re-air must start after <see cref="EventEndUtc"/> and last
    /// roughly as long, so a highlights show isn't mistaken for a full replay.</summary>
    public long EventStartUtc { get; set; }
    public long EventEndUtc { get; set; }

    public RescueTicketState State { get; set; } = RescueTicketState.Open;

    public long CreatedUtc { get; set; }
    public long? LastSweepUtc { get; set; }
    /// <summary>Earliest next EPG sweep for this ticket (rate-limits per-ticket work).</summary>
    public long NextSweepUtc { get; set; }
    /// <summary>Give up (→ GaveUp) once now passes this with no re-air found.</summary>
    public long ExpiresUtc { get; set; }

    /// <summary>The scheduled replay recording, once one is armed. Null while still hunting.</summary>
    public int? ReplayRecordingId { get; set; }
    /// <summary>Search every channel on the mapped sources, not just the league's mapped channels.</summary>
    public bool WholeSource { get; set; }
    /// <summary>Why the ticket opened (failure reason) / latest sweep note.</summary>
    public string? Note { get; set; }
}
