namespace DVarr.Data.Entities;

/// <summary>
/// A channel as reachable on ONE specific credential (docs/05 §1.2, docs/06 §3.3).
/// Channels are keyed PER SOURCE: the same logical channel (e.g. "Sky Sports F1")
/// can exist as separate rows on several credentials, each with its own
/// <see cref="SourceId"/>/<see cref="StreamId"/>. <see cref="LogicalKey"/> (and
/// <see cref="NameNorm"/>) lets the UI merge those rows for the "all sources" view,
/// while <see cref="SourceId"/> lets it filter to one source — the data backing
/// for the source toggle.
/// </summary>
public class Channel
{
    public int Id { get; set; }

    /// <summary>The credential this channel row belongs to — load-bearing for the same-source/same-credential rule.</summary>
    public int SourceId { get; set; }
    public ProviderSource? Source { get; set; }

    public int? ChannelNumber { get; set; }
    public string Name { get; set; } = "";
    public string NameNorm { get; set; } = "";

    /// <summary>Groups per-source rows of the same logical channel for the "all sources" view.</summary>
    public string? LogicalKey { get; set; }

    public string? EpgChannelId { get; set; }

    /// <summary>tvg-id resolved by NAME-matching this channel against the EPG's channel list, used when the provider
    /// left <see cref="EpgChannelId"/> blank (most non-sports channels). Mirrors how TiviMate matches by name so the
    /// guide fills in for almost every channel. Recomputed on each EPG sync. Effective id = EpgChannelId ?? MatchedEpgId.</summary>
    public string? MatchedEpgId { get; set; }

    /// <summary>Category/group from the provider (Xtream get_live_categories) — drives the per-source group filter.</summary>
    public string? GroupName { get; set; }

    public int StreamId { get; set; }

    /// <summary>Optional explicit stream URL. When set, the recorder uses it verbatim instead of
    /// building the Xtream .ts URL — used for manual/test channels (e.g. a public test stream),
    /// so the recorder can be exercised without contacting the provider.</summary>
    public string? DirectUrl { get; set; }

    public bool TvArchive { get; set; }
    public int? TvArchiveDuration { get; set; }
    public string? DetectedQuality { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>User pref: favourite channels sort to the top of every channel list/picker. Survives re-ingest
    /// (ingest upserts existing rows by (SourceId, StreamId) and never touches this flag).</summary>
    public bool Favorite { get; set; }

    /// <summary>User pref: hidden channels are excluded from the Channels page, Guide and channel pickers by default
    /// (a provider lineup can be 35k channels — most irrelevant for sports). Explicit league mappings and existing
    /// recordings still work — this is a browse-time filter only. Survives re-ingest like <see cref="Favorite"/>.</summary>
    public bool Hidden { get; set; }

    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}

/// <summary>
/// Per-source user preference for one provider group/category: hide it from every group list and channel view
/// (a 650-group lineup is mostly noise for a sports DVR) or favourite it (sorts first in group dropdowns).
/// Keyed by (SourceId, GroupName) so prefs survive channel re-ingests (group rows are re-upserted, prefs are not
/// touched). Rows exist only for groups the user has actually marked — absence = normal visibility.
/// </summary>
public class ChannelGroupPref
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public string GroupName { get; set; } = "";
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
}

/// <summary>
/// Disposable EPG cache (docs/05 §1.2, docs/06 §3.3). Decoupled from the channel list:
/// programmes are keyed by the XMLTV channel id (<see cref="EpgChannelId"/>) PER SOURCE,
/// so the ENTIRE external EPG is stored even for channels not (yet) in the lineup. The
/// guide joins <see cref="Channel.EpgChannelId"/> → <see cref="EpgChannelId"/> at render
/// time. <see cref="EpgUid"/> = epg_channel_id + start_utc is stable across refreshes
/// (bug #6): a recording is never cancelled or re-timed because a programme id changed.
/// </summary>
public class Programme
{
    public int Id { get; set; }

    /// <summary>The credential whose EPG this programme came from (provider xmltv.php or the per-source external override).</summary>
    public int SourceId { get; set; }

    /// <summary>The XMLTV <c>channel</c> id (tvg-id), matched to <see cref="Channel.EpgChannelId"/> at render time.</summary>
    public string EpgChannelId { get; set; } = "";

    public long StartUtc { get; set; }
    public long StopUtc { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string EpgUid { get; set; } = "";
}
