namespace DVarr.Data;

/// <summary>
/// The canonical recording state machine (docs/05 §2). Stored as TEXT.
/// Invariant: never a terminal failure state before post-roll completes —
/// recoverable stalls/exits go through Recovering/FailingOver/Degraded.
/// </summary>
public enum RecordingState
{
    Pending,
    Starting,
    Recording,
    Recovering,
    FailingOver,
    Degraded,
    Stopping,
    Finalizing,
    Done,
    FinalizeRetry,
    NeedsAttention,
    /// <summary>Demand exceeded both logins for this window: the recording lost the conflict policy and is parked
    /// here with a reason. Holds NO credential slot; the scheduler re-evaluates it each tick and promotes it back
    /// to Pending if a slot frees (docs/05 conflict planning).</summary>
    Conflict,
    Missed,
    /// <summary>Stopped by the user before it ever captured (Pending/Conflict cancelled). Terminal; never re-armed.
    /// A user-stop of a LIVE recording instead finalizes the partial capture to Done.</summary>
    Cancelled,
}

public enum RecordingPriority { CantMiss, Normal, Opportunistic }

public enum ConflictPolicy { Refuse, Queue, Preempt }

/// <summary>What a tuner lease is for. A credential's single slot is consumed by any of these.</summary>
public enum LeasePurpose { Recording, Probe, Live, Relay }

public enum LeaseState { Active, Released, Orphaned }

public enum CaptureChain { A, B }

public enum ContentVerdict { Unverified, Ok, Black, Frozen, Silent, PlaceholderSuspect }

public enum JobKind { EpgSync, LeagueSync, ChannelQa, WalCheckpoint, LitestreamVerify }

public enum JobState { Pending, Running, Done, Error }

public enum NotificationKind
{
    Started, Completed, Failed, Missed, StalledRelaunched, FailedOver,
    PlaceholderDetected, Degraded, NeedsAttention, Conflict, Cancelled,
    // Appended only (int-stored — existing rows keep their meaning):
    EpgRepick,     // arm-window guide match moved a recording to the channel actually showing the event
    Unresolvable,  // a monitored event can't be scheduled (league has no usable channel mapping)
    AutoExtended,  // smart auto-stop adjusted a live recording's window from the guide's live status (extended / capped / closed)
    Unmatched,     // a finished manual/guide recording couldn't be auto-matched to a game — staged for manual Import
    LowDiskSpace,  // a filesystem is under its free-space floor, or a new recording is projected to breach it
    ReplayHunting,   // opened a second-chance rescue ticket — hunting the guide for a re-air of a failed game
    ReplayScheduled, // found a re-air and scheduled a low-priority replay recording
    ReplayGaveUp,    // no re-air appeared before the rescue ticket expired
    RetentionEvicted, // retention policy deleted one or more old recordings to reclaim space
    Retimed,          // the provider moved an event's start and DVarr retimed the pending recording to match
    NoGuideMatch,     // nothing in the lineup's guide shows this fixture — likely a national/streaming-only broadcast
}

/// <summary>Lifecycle of a second-chance replay-rescue ticket (docs: automatic re-air rescue).</summary>
public enum RescueTicketState
{
    /// <summary>Still hunting the guide for a re-air.</summary>
    Open,
    /// <summary>A re-air was found and a replay recording is armed.</summary>
    Scheduled,
    /// <summary>A good copy landed (the replay finished, or the event was recorded some other way).</summary>
    Closed,
    /// <summary>No re-air appeared before the ticket expired.</summary>
    GaveUp,
    /// <summary>Cancelled by the user.</summary>
    Cancelled,
}

public enum Severity { Info, Warn, Critical }

/// <summary>How a library item came to exist (stored as TEXT like every enum).</summary>
public enum LibraryItemOrigin { Recorded, Adopted }

/// <summary>Whether the library item's file was present at the last reconciling scan.</summary>
public enum LibraryItemStatus { Ok, Missing }

public enum EventStatus { Scheduled, Live, Completed, Postponed, Cancelled, Unknown }
