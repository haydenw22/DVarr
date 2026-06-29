using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

public sealed record EventSyncResult(int LeagueId, bool Ok, int Fetched, int Added, int Updated, string? Error);

/// <summary>
/// Syncs a league's events from its provider and upserts them by a stable, source-independent natural key
/// (<c>{leagueId}:{provider}:{externalId}</c>). NEVER deletes or re-keys, so a re-sync can never orphan a
/// recording (bug #4). Times update on re-sync (the event genuinely moved); the auto-scheduler re-resolves.
/// </summary>
public sealed class EventIngestService
{
    private readonly DVarrDbContext _db;
    private readonly EventFetcher _fetcher;
    private readonly TheSportsDbClient _tsdb;
    private readonly DbWriteGate _gate;
    private readonly SettingsService _settings;
    private readonly ILogger<EventIngestService> _log;

    public EventIngestService(DVarrDbContext db, EventFetcher fetcher, TheSportsDbClient tsdb, DbWriteGate gate, SettingsService settings, ILogger<EventIngestService> log)
    { _db = db; _fetcher = fetcher; _tsdb = tsdb; _gate = gate; _settings = settings; _log = log; }

    public async Task<EventSyncResult> SyncLeagueAsync(int leagueId, CancellationToken ct = default)
    {
        var l = await _db.Leagues.FindAsync(new object?[] { leagueId }, ct);
        if (l is null) return new EventSyncResult(leagueId, false, 0, 0, 0, "league not found");
        if (l.EventProvider == "manual") return new EventSyncResult(leagueId, true, 0, 0, 0, null);

        List<IngestedEvent> events;
        try { events = await _fetcher.FetchAsync(l, ct); }
        catch (Exception ex) { _log.LogError(ex, "[Events] fetch failed for league {Id}", leagueId); return new EventSyncResult(leagueId, false, 0, 0, 0, ex.Message); }

        int added = 0, updated = 0;
        var now = EpochTime.Now();
        var isTsdb = l.EventProvider == "thesportsdb";
        // Per-sport assumed duration for events whose provider gives no end time (every TheSportsDB event).
        // ICS events that DO carry a real DTEND keep it (the ?? below only fills when EndUtc is null).
        var defaultDurationS = await _settings.GetEventDurationSecondsAsync(l.Sport, l.EventDurationOverrideS);
        long? EndFor(IngestedEvent ie) => ie.EndUtc ?? ie.StartUtc + defaultDurationS;

        // Refresh league artwork (poster/badge) from TheSportsDB so the Plex provider + media import have it.
        if (isTsdb && !string.IsNullOrWhiteSpace(l.ExternalLeagueId))
        {
            try
            {
                var meta = await _tsdb.LookupLeagueAsync(l.ExternalLeagueId!, ct);
                if (meta is not null) { l.PosterUrl = meta.Poster ?? l.PosterUrl; l.BadgeUrl = meta.Badge ?? l.BadgeUrl; }
            }
            catch (Exception ex) { _log.LogDebug(ex, "[Events] league artwork lookup failed for {Id}", leagueId); }
        }

        await _gate.WriteAsync(async () =>
        {
            // Snapshot existing events INSIDE the gate (which serializes all writers) so a concurrent same-league sync
            // can't insert the same NaturalKey between our read and write and roll back the whole batch on the unique index.
            var existing = await _db.Events.Where(e => e.LeagueId == leagueId).ToDictionaryAsync(e => e.NaturalKey, ct);
            foreach (var ie in events)
            {
                var nk = $"{l.Id}:{l.EventProvider}:{ie.ExternalId}";
                if (existing.TryGetValue(nk, out var ev))
                {
                    ev.Title = ie.Title;
                    ev.StartUtc = ie.StartUtc;
                    ev.EndUtc = EndFor(ie);
                    ev.StartIsDateOnly = ie.DateOnly;
                    // Only let the provider move the status when it actually reported one. ICS always reports null,
                    // and a blind MapStatus(null)=Scheduled would clobber a Live/Completed/Cancelled event on re-sync.
                    if (!string.IsNullOrWhiteSpace(ie.Status)) ev.Status = MapStatus(ie.Status);
                    if (ie.ThumbUrl is not null) ev.ThumbUrl = ie.ThumbUrl;
                    if (ie.Round is not null) ev.Round = ie.Round;
                    if (ie.Season is not null) ev.Season = ie.Season;
                    if (ie.HomeTeamId is not null) ev.HomeTeamId = ie.HomeTeamId;
                    if (ie.AwayTeamId is not null) ev.AwayTeamId = ie.AwayTeamId;
                    if (isTsdb) ev.TsdbEventId = ie.ExternalId;
                    ev.LastSeenSyncUtc = now;
                    updated++;
                }
                else
                {
                    _db.Events.Add(new Event
                    {
                        LeagueId = l.Id, NaturalKey = nk, Title = ie.Title,
                        StartUtc = ie.StartUtc, EndUtc = EndFor(ie), StartIsDateOnly = ie.DateOnly,
                        Status = MapStatus(ie.Status), Monitored = l.Monitored, LastSeenSyncUtc = now,
                        ThumbUrl = ie.ThumbUrl, Round = ie.Round, Season = ie.Season,
                        HomeTeamId = ie.HomeTeamId, AwayTeamId = ie.AwayTeamId,
                        TsdbEventId = isTsdb ? ie.ExternalId : null,
                    });
                    added++;
                }
            }
            l.LastEventSyncUtc = now;
            await _db.SaveChangesAsync(ct);
        }, ct);

        _log.LogInformation("[Events] League {Id} ({Name}): fetched {F} ({A} new, {U} updated)", leagueId, l.Name, events.Count, added, updated);
        return new EventSyncResult(leagueId, true, events.Count, added, updated, null);
    }

    private static EventStatus MapStatus(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return EventStatus.Scheduled;
        var u = s.ToUpperInvariant();
        if (u is "FT" || u.Contains("FINISH") || u.Contains("FINAL")) return EventStatus.Completed;
        if (u.Contains("POSTPON")) return EventStatus.Postponed;
        if (u.Contains("CANCEL")) return EventStatus.Cancelled;
        if (u.Contains("LIVE") || u.Contains("IN PROGRESS") || u.Contains("1H") || u.Contains("2H")) return EventStatus.Live;
        return EventStatus.Scheduled;
    }
}
