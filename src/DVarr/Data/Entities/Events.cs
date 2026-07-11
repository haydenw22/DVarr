namespace DVarr.Data.Entities;

public class League
{
    public int Id { get; set; }
    public string Sport { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Monitored { get; set; } = true;

    /// <summary>Where events come from. TheSportsDB is the only provider offered in the UI now;
    /// "manual"/"ics" remain as legacy column values for older rows but are no longer created.</summary>
    public string EventProvider { get; set; } = "thesportsdb";
    /// <summary>TheSportsDB numeric league id (idLeague). Required for thesportsdb leagues.</summary>
    public string? ExternalLeagueId { get; set; }
    /// <summary>Legacy: external ICS calendar feed (no longer offered in the UI).</summary>
    public string? IcsUrl { get; set; }

    /// <summary>TheSportsDB portrait poster (strPoster) — used as the Plex show poster + UI tile.</summary>
    public string? PosterUrl { get; set; }
    /// <summary>TheSportsDB square badge/crest (strBadge) — fallback icon.</summary>
    public string? BadgeUrl { get; set; }

    /// <summary>User-chosen calendar colour (hex, one of 10 palette options) for this league's event cards.</summary>
    public string? Color { get; set; }

    /// <summary>Auto-schedule monitored events whose start is within this many days.</summary>
    public int ScheduleHorizonDays { get; set; } = 14;

    /// <summary>Per-league assumed event duration (seconds) when the provider gives no end time. Null = fall back to
    /// the per-sport override (event_duration_overrides_json) then default_event_duration_s.</summary>
    public int? EventDurationOverrideS { get; set; }

    /// <summary>Team-follow: JSON array of {id,name} TheSportsDB teams to record in this league. Null/empty = ALL
    /// teams (record every match). The full schedule is still ingested for correct episode numbering; this only
    /// filters which events the auto-scheduler arms (an event is kept if either side's team id is in this set).</summary>
    public string? MonitoredTeamsJson { get; set; }

    /// <summary>Motorsport session-follow: JSON array of session kinds to record (e.g. ["Race","Qualifying"]; kinds from
    /// <see cref="DVarr.Services.Events.MotorsportSession"/>). Null = ALL sessions. Like team-follow, the full schedule
    /// is still ingested; this only filters which sessions the scheduler arms and which show on the calendar.</summary>
    public string? MonitoredSessionsJson { get; set; }

    /// <summary>Motorsport per-session duration overrides: JSON map of session kind → seconds (e.g. {"Race":10800,
    /// "Practice 1":3600}) for the assumed length when the provider gives no end time. Beats EventDurationOverrideS
    /// for a matching session; unset kinds fall through to the normal league/sport/default resolution.</summary>
    public string? SessionDurationsJson { get; set; }

    /// <summary>Smart auto-stop mode for this league's recordings. Null or "auto" = Auto (AutoStopService may extend a
    /// live recording's end while the guide says the event is still in play — extra time/penalties — and close it once
    /// the provider reports a terminal status). "fixed" = Fixed (never touch the window; record exactly the scheduled
    /// window + pads, the pre-Phase-21 behaviour).</summary>
    public string? AutoStopMode { get; set; }

    /// <summary>Cap (seconds) on the TOTAL auto-stop extension beyond the event's scheduled end for this league.
    /// Null = the per-sport default (2h for motorsport — "keep full race endings" — else 1h). Only meaningful when
    /// <see cref="AutoStopMode"/> is Auto.</summary>
    public int? AutoStopMaxExtendS { get; set; }

    public long? LastEventSyncUtc { get; set; }

    /// <summary>Conflict tie-breaker (higher wins) when demand exceeds both logins for a window: a higher-priority
    /// league's event keeps/takes the slot and a lower one is parked in Conflict. 0 = normal (docs/05 conflict planning).</summary>
    public int Priority { get; set; }

    public long CreatedUtc { get; set; }

    public ICollection<Event> Events { get; set; } = new List<Event>();
    public ICollection<LeagueChannelMap> ChannelMaps { get; set; } = new List<LeagueChannelMap>();
}

/// <summary>
/// The immutable internal event identity (docs/05 §1.2, docs/06 §2.4). Recordings
/// reference Id — a source re-key/re-sync can NEVER orphan a recording (bug #4).
/// All instants are epoch seconds (bug #5). The *_locked latches make user intent
/// durable against any sync pass (bug #4).
/// </summary>
public class Event
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League? League { get; set; }

    /// <summary>Deterministic, source-independent (e.g. f1:2026:r09-barcelona:race) — survives source re-keying.</summary>
    public string NaturalKey { get; set; } = "";

    public string Title { get; set; } = "";
    public EventStatus Status { get; set; } = EventStatus.Scheduled;

    /// <summary>Authoritative UTC instant, epoch seconds (bug #5).</summary>
    public long StartUtc { get; set; }
    public long? EndUtc { get; set; }

    /// <summary>1 for date-only/all-day events; anchored display-zone-midnight→UTC (bug #11).</summary>
    public bool StartIsDateOnly { get; set; }

    public bool Monitored { get; set; } = true;

    /// <summary>Latch — once the user touches it, no sync/filter overrides it (bug #4).</summary>
    public bool MonitoredLocked { get; set; }

    /// <summary>Latch — manual channel override; re-resolve respects it (bug #3).</summary>
    public bool ChannelLocked { get; set; }

    // ---- TheSportsDB enrichment (drives Plex episode numbering + artwork; bug #9) ----
    /// <summary>TheSportsDB idEvent (stable PK on their side) for re-lookup + artwork refresh.</summary>
    public string? TsdbEventId { get; set; }
    /// <summary>Event thumbnail (strThumb) → Plex episode thumbnail; falls back to the league poster.</summary>
    public string? ThumbUrl { get; set; }
    /// <summary>intRound (informational). NOT the Plex episode index — the agent + media import use a per-season
    /// StartUtc ordinal (sessions share a round). Round is only the fallback episode number on the manual-match path.</summary>
    public int? Round { get; set; }
    /// <summary>strSeason (e.g. "2026" or "2025-2026") — used for the Plex season.</summary>
    public string? Season { get; set; }

    /// <summary>TheSportsDB idHomeTeam / idAwayTeam — drive the per-league team-follow filter (record only chosen
    /// teams' matches). Null for non-team-vs-team sports (motorsport).</summary>
    public string? HomeTeamId { get; set; }
    public string? AwayTeamId { get; set; }

    public long? LastSeenSyncUtc { get; set; }
    public string? SourceMetaJson { get; set; }

    public ICollection<EventSession> Sessions { get; set; } = new List<EventSession>();
}

/// <summary>Per-session recordable item (FP1/FP2/FP3/Q/SQ/Sprint/Race), each with its own immutable id + latches.</summary>
public class EventSession
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event? Event { get; set; }

    public string NaturalKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";

    public long StartUtc { get; set; }
    public long? EndUtc { get; set; }
    public bool StartIsDateOnly { get; set; }

    public bool Monitored { get; set; } = true;
    public bool MonitoredLocked { get; set; }
    public bool ChannelLocked { get; set; }
}

/// <summary>The only place a volatile provider id is stored — for audit/repair (docs/06).</summary>
public class SourceLink
{
    public int Id { get; set; }
    public int InternalEventId { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderEventId { get; set; } = "";
    public long FirstSeenUtc { get; set; }
    public long LastSeenUtc { get; set; }
}

/// <summary>
/// League↔channel mapping (docs/05 §1.2; docs/06 §3.4). A pinned=1, rank=1 row is
/// the user's preferred channel and carries a dominant PIN_FLOOR in the resolver,
/// which dirty EPG data can never outrank (bug #2).
/// </summary>
public class LeagueChannelMap
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League? League { get; set; }
    public int ChannelId { get; set; }

    public int Rank { get; set; }
    public bool Pinned { get; set; }

    /// <summary>Optional team scope (TheSportsDB team id): when set, this mapping applies ONLY to events this team
    /// plays in (home or away) — e.g. within one MLB league, Yankees→YES Network and Mets→SNY. Null = league-wide.
    /// A team-scoped mapping outranks every league-wide one for its team's games (see ResolverService.TeamFloor).</summary>
    public string? TeamId { get; set; }
    /// <summary>Display-name snapshot for the scoped team (UI only; the id is authoritative).</summary>
    public string? TeamName { get; set; }

    /// <summary>Denormalised owning credential (docs/06 §3.4).</summary>
    public int? SourceId { get; set; }

    public long? ActiveFromUtc { get; set; }
    public long? ActiveToUtc { get; set; }
}
