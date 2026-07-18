namespace DVarr.Data.Entities;

/// <summary>
/// A durable, stateful wall-clock recording window — the source of truth for the
/// scheduler (docs/05 §3) and the supervisor (docs/04). It references the internal
/// event/session id (never a source id, never a frozen event copy) and carries a
/// resolution snapshot so it can be re-resolved in place (bug #3).
/// </summary>
public class Recording
{
    public int Id { get; set; }

    public int? EventId { get; set; }
    public int? SessionId { get; set; }

    /// <summary>Chosen credential — the single 1-slot login this recording occupies. Drives the same-source (== same-credential) fallback rule (#7).</summary>
    public int SourceId { get; set; }
    public int ChannelId { get; set; }

    /// <summary>Xtream stream_id on SourceId, for the direct .ts URL (D3).</summary>
    public int StreamId { get; set; }

    public long StartUtc { get; set; }
    public long EndUtc { get; set; }
    public int PrePadS { get; set; }
    public int PostPadS { get; set; }

    public RecordingPriority Priority { get; set; } = RecordingPriority.Normal;

    /// <summary>Set when the user manually places this recording on a channel (reassign) — the arm-window EPG
    /// re-pick must never move a locked recording; a manual choice is durable.</summary>
    public bool ChannelLocked { get; set; }

    /// <summary>OFF at v1 (D4); default false. Same-credential dual capture is impossible (one login = one slot).</summary>
    public bool DualCapture { get; set; }

    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Refuse;

    /// <summary>The docs/05 §2 state machine column.</summary>
    public RecordingState State { get; set; } = RecordingState.Pending;

    public int AttemptCount { get; set; }
    /// <summary>One-shot guard: set once the scheduler has made the guaranteed "second attempt at the event's real
    /// start time" for a recording whose pre-roll attempt captured nothing (the retry_at_event_start feature).</summary>
    public bool EventStartRetried { get; set; }
    public int? FfmpegPid { get; set; }
    public string? SegmentDir { get; set; }
    public string? OutputPath { get; set; }

    public long BytesWritten { get; set; }
    public long? LastFrameUtc { get; set; }
    public long? LastContentOkUtc { get; set; }

    /// <summary>List of {start_utc, duration_s} true-missing-time gaps (docs/04 §5.4).</summary>
    public string? GapsJson { get; set; }

    /// <summary>Live match-status transitions observed while recording — JSON array of {t (epoch s), s (raw
    /// TheSportsDB status), p (match minute)} appended by AutoStopService. Finalize turns them into embedded
    /// MKV chapters (Kick-off / Half-time / Extra time / Penalty shoot-out…). Null = none observed.</summary>
    public string? LiveMarksJson { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>{resolved_channel_id, fallbacks[], score, confidence, resolver_version, resolved_at} — a snapshot, not a frozen event copy (bug #3).</summary>
    public string? ResolutionSnapshotJson { get; set; }

    public string? Title { get; set; }

    /// <summary>For a manual recording with no linked Event: a free-text name to match against TheSportsDB at
    /// finalize, so the file is renamed to a Plex-appropriate "League/Season/Event" with artwork. Null = leave flat.</summary>
    public string? MatchQuery { get; set; }

    /// <summary>Set on a REPLAY recording scheduled by the second-chance rescue sweep — links it back to the ticket
    /// that produced it (and marks it as a replay in the UI). Null for a normal recording.</summary>
    public int? RescueTicketId { get; set; }

    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }

    public ICollection<RecordingFallback> Fallbacks { get; set; } = new List<RecordingFallback>();
    public ICollection<RecordingSegment> Segments { get; set; } = new List<RecordingSegment>();
}

/// <summary>
/// Ordered, same-source-constrained fallback channels. "Same source" means SAME CREDENTIAL:
/// the (RecordingId, SourceId) composite FK to Recording(Id, SourceId) makes a cross-credential
/// fallback UNREPRESENTABLE at the DB layer — the structural fix for bug #7 (docs/05 §1.4).
/// </summary>
public class RecordingFallback
{
    public int Id { get; set; }
    public int RecordingId { get; set; }
    public Recording? Recording { get; set; }

    public int Rank { get; set; }
    public int ChannelId { get; set; }

    /// <summary>INVARIANT: composite FK (RecordingId, SourceId) → Recording(Id, SourceId) forces this == Recording.SourceId.</summary>
    public int SourceId { get; set; }
}

/// <summary>
/// One row per captured MPEG-TS segment. The A (primary) chain is the only chain at v1
/// (dual capture OFF — D4). Makes finalize curatable and lets catch-up-on-boot
/// re-finalize a FINALIZING window whose segments still exist (docs/05 §1.5, §3.4).
/// </summary>
public class RecordingSegment
{
    public int Id { get; set; }
    public int RecordingId { get; set; }

    public CaptureChain Capture { get; set; } = CaptureChain.A;
    public int Seq { get; set; }
    public string Path { get; set; } = "";
    public long StartUtc { get; set; }
    public int DurationMs { get; set; }
    public long Bytes { get; set; }
    public bool Closed { get; set; }
    public ContentVerdict ContentVerdict { get; set; } = ContentVerdict.Unverified;
    public bool Suspect { get; set; }
}
