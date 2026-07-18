namespace DVarr.Data.Entities;

/// <summary>
/// One video file the library knows about — the durable record of what is PHYSICALLY on the media drive
/// (the Sonarr "EpisodeFile" idea). Created when a finished recording is filed by the media import, and by
/// the reconciling disk scan (which also adopts files DVarr didn't create and flags files that vanished).
/// Deliberately survives deletion of its Recording / Event / League rows: every provenance link is nullable
/// and severs to SetNull, and the display fields below are SNAPSHOTS taken at import time, so the library
/// keeps showing what a file is even after the request that produced it is long gone.
/// </summary>
public class LibraryItem
{
    public int Id { get; set; }

    // ---- provenance links (nullable; severed, never cascaded, when the pointed-to row is deleted) ----
    public int? RecordingId { get; set; }
    public int? EventId { get; set; }
    public int? LeagueId { get; set; }

    // ---- display metadata (snapshotted — league/event deletion must not blank the library) ----
    /// <summary>League display name == the Plex "show" folder (e.g. "FIFA World Cup").</summary>
    public string ShowName { get; set; } = "";
    public string? Sport { get; set; }
    /// <summary>Plex season == year. 0 for unsorted/flat files that haven't been filed yet.</summary>
    public int SeasonYear { get; set; }
    /// <summary>Plex episode number within the season (the SyyyyEnn ordinal). 0 when unknown.</summary>
    public int EpisodeNum { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Game start (epoch s) — event start when linked, else the recording window start, else file date.</summary>
    public long StartUtc { get; set; }

    // ---- file truth ----
    /// <summary>Absolute path on disk. Unique — one row per physical file.</summary>
    public string FilePath { get; set; } = "";
    public long FileBytes { get; set; }
    public int? DurationS { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? Height { get; set; }

    // ---- capture provenance snapshots (labels, not FKs — cheap and deletion-proof) ----
    public string? ChannelName { get; set; }
    public string? SourceLabel { get; set; }

    /// <summary>Recorded = DVarr captured and filed it; Adopted = the disk scan found it (pre-existing file,
    /// an external copy, or an import failure that left the file behind).</summary>
    public LibraryItemOrigin Origin { get; set; } = LibraryItemOrigin.Recorded;

    /// <summary>Ok = file verified on disk; Missing = the last scan couldn't find it (deleted/moved externally,
    /// or the media share was offline). A Missing item heals back to Ok if the file reappears.</summary>
    public LibraryItemStatus Status { get; set; } = LibraryItemStatus.Ok;
    public long? MissingSinceUtc { get; set; }

    /// <summary>True while the file sits in ".unsorted" (or flat in the media root) awaiting a manual Import
    /// into the League/Season/Game layout.</summary>
    public bool Unsorted { get; set; }

    /// <summary>User "protect" flag — retention eviction never deletes a protected item (grand finals, keepsakes).</summary>
    public bool Protected { get; set; }

    /// <summary>When a media server reported this file watched (Plex/Jellyfin scrobble webhook), epoch s. Null =
    /// unwatched. Drives the "delete after watched" retention mode.</summary>
    public long? WatchedUtc { get; set; }

    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}
