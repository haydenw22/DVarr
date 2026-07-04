namespace DVarr.Data.Entities;

/// <summary>
/// One row per IPTV credential/login. Each credential = exactly ONE concurrent
/// stream slot (provider-fixed; docs/05 §1.2, docs/07 §2.1). Concurrency comes
/// from managing multiple ProviderSource rows, not multiple slots per credential.
/// </summary>
public class ProviderSource
{
    public int Id { get; set; }

    /// <summary>src1 / src2 / … / srcN — one per provider login.</summary>
    public string Label { get; set; } = "";

    /// <summary>xtream | m3u (only xtream is fully implemented for now).</summary>
    public string Type { get; set; } = "xtream";

    public string BaseUrl { get; set; } = "";
    public int Port { get; set; }
    public int? HttpsPort { get; set; }
    public string ServerProtocol { get; set; } = "http";

    /// <summary>Optional external XMLTV EPG URL (.xml or .xml.gz), independent of the provider's own EPG.</summary>
    public string? EpgUrl { get; set; }

    /// <summary>When true (and EpgUrl is set), EPG sync uses the external EpgUrl instead of the provider's xmltv.php.</summary>
    public bool EpgOverride { get; set; }

    /// <summary>Optional HTTP user-agent override for provider requests.</summary>
    public string? UserAgent { get; set; }

    // Encrypted at rest (docs/05 §6). Encryption layer is wired in a later slice;
    // for now the column exists and is masked at the API boundary.
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Provider-fixed at 1 — exactly one concurrent stream per credential. Not a tunable (D6).</summary>
    public int MaxStreams { get; set; } = 1;

    // Last-seen user_info advisory cross-check only (should report 1 concurrent allowed).
    public int? ProviderMaxConns { get; set; }
    public int? ProviderActiveCons { get; set; }

    public string? AllowedOutputFormats { get; set; }
    public long? ExpDateUtc { get; set; }
    public string? Status { get; set; }
    public bool IsTrial { get; set; }
    public bool Healthy { get; set; }
    public long? LastAuthAtUtc { get; set; }

    /// <summary>Epoch seconds of the last SUCCESSFUL EPG sync for this source. Stamped in the ingest's success path;
    /// null until the first good sync. The EPG re-pick sweep uses it to trigger a refresh when the guide is &gt;12h stale.</summary>
    public long? LastEpgSyncUtc { get; set; }

    public bool Enabled { get; set; } = true;
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}

/// <summary>
/// Slot usage for a credential. At most ONE active lease per source_id at any instant
/// (each credential has exactly one slot). Heartbeat is tied to segment-byte progress,
/// not process liveness (docs/05 §1.2, docs/07 §3.3). The lease table is the source of
/// truth for slot occupancy and is reconciled on boot.
/// </summary>
public class TunerLease
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public ProviderSource? Source { get; set; }

    public LeasePurpose Purpose { get; set; } = LeasePurpose.Recording;
    public int? RecordingId { get; set; }
    public int? ChannelId { get; set; }
    public int? StreamId { get; set; }
    public int? Pid { get; set; }

    public long AcquiredAtUtc { get; set; }
    public long LastHeartbeatUtc { get; set; }
    public long? DeadlineUtc { get; set; }
    public LeaseState State { get; set; } = LeaseState.Active;
    public long BytesWritten { get; set; }

    /// <summary>Runtime-only exactly-once guard so a lease's slot is released exactly once even if Release is called
    /// from two paths (e.g. a preview's error handler and the session sweeper) — double-release would free another
    /// session's slot and break the one-stream-per-credential rule. Not persisted.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public int ReleasedFlag;
}

/// <summary>Probe results per channel (docs/07 §6.3).</summary>
public class ChannelHealth
{
    public int Id { get; set; }
    public int ChannelId { get; set; }

    public bool Alive { get; set; }
    public int? BitrateKbps { get; set; }
    public double BlackRatio { get; set; }
    public double FreezeRatio { get; set; }
    public double SilenceRatio { get; set; }
    public ContentVerdict Verdict { get; set; } = ContentVerdict.Unverified;
    public int ConsecutiveFailures { get; set; }
    public string? FingerprintHashesJson { get; set; }
    public long? LastProbedUtc { get; set; }
}
