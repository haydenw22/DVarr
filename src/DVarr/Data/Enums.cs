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
    PlaceholderDetected, Degraded, NeedsAttention, Conflict,
}

public enum Severity { Info, Warn, Critical }

public enum EventStatus { Scheduled, Live, Completed, Postponed, Cancelled, Unknown }
