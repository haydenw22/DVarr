namespace DVarr.Data.Entities;

/// <summary>Durable model for non-recording periodic work (EPG/league sync, channel QA, WAL checkpoint). NOT a recording runner (docs/05 §1.6).</summary>
public class Job
{
    public int Id { get; set; }
    public JobKind Kind { get; set; }
    public JobState State { get; set; } = JobState.Pending;
    public long RunAtUtc { get; set; }
    public long? StartedUtc { get; set; }
    public long? FinishedUtc { get; set; }
    public int? IntervalS { get; set; }
    public string? LastError { get; set; }
    public string? PayloadJson { get; set; }
}

/// <summary>One row per scheduler pass — observability that the scheduler is alive and what it did (docs/05 §1.6).</summary>
public class ScheduleTick
{
    public int Id { get; set; }
    public long TickUtc { get; set; }
    public int RecordingsExamined { get; set; }
    public int Started { get; set; }
    public int Resumed { get; set; }
    public int Finalized { get; set; }
    public int Missed { get; set; }
    public int Conflicts { get; set; }
    public int DurationMs { get; set; }
}

/// <summary>Typed configuration as a key/value table with JSON/scalar values (docs/05 §1.7).</summary>
public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public long UpdatedUtc { get; set; }
}

/// <summary>
/// Durable record of every state transition and alert; also the feed for SSE and
/// the Home Assistant hooks (docs/05 §1.8). Reliability-critical kinds (Missed,
/// StalledRelaunched, PlaceholderDetected) are first-class so Hayden gets an alert
/// the moment a can't-miss capture is at risk.
/// </summary>
public class Notification
{
    public int Id { get; set; }
    public int? RecordingId { get; set; }
    public long TsUtc { get; set; }
    public NotificationKind Kind { get; set; }
    public Severity Severity { get; set; } = Severity.Info;
    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public string? Message { get; set; }
    public string? PayloadJson { get; set; }

    /// <summary>1 once pushed to Home Assistant via webhook/REST (primary; MQTT optional-future).</summary>
    public bool DeliveredHa { get; set; }
}

/// <summary>Secrets at rest: Sonarr-emulation API key, provider/Plex/HA tokens (docs/05 §6). Encrypted-at-rest layer wired in a later slice.</summary>
public class SecretEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}
