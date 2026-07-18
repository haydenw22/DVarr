using System.Diagnostics;
using System.Text;
using DVarr.Data;
using DVarr.Infrastructure;
using DVarr.Services.Events;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Media;

/// <summary>
/// After finalize, files an event-linked (or TheSportsDB-matched) recording into a Plex/Jellyfin TV-Shows layout
/// (league = show, year = season, per-season ordinal = episode) with Kodi/NFO sidecars and artwork ON DISK —
/// show poster (poster.jpg), season poster (Season&lt;year&gt;.jpg), and an episode thumbnail named to match the
/// video basename (Plex's stock Local Media convention). TheSportsDB posters/thumbs are downloaded so the library
/// is rich even without the DVarr Plex agent; with the agent, the same league/year/ordinal numbering matches the
/// provider's episode index. Recordings with neither an Event nor a match query keep their flat name.
/// </summary>
public sealed class MediaImportService
{
    private readonly DVarrDbContext _db;
    private readonly FfmpegLocator _ffmpeg;
    private readonly RuntimePaths _paths;
    private readonly DbWriteGate _gate;
    private readonly TheSportsDbClient _tsdb;
    private readonly IHttpClientFactory _httpFactory;
    private readonly LibraryService _library;
    private readonly ILogger<MediaImportService> _log;

    public MediaImportService(DVarrDbContext db, FfmpegLocator ffmpeg, RuntimePaths paths, DbWriteGate gate,
        TheSportsDbClient tsdb, IHttpClientFactory httpFactory, LibraryService library, ILogger<MediaImportService> log)
    { _db = db; _ffmpeg = ffmpeg; _paths = paths; _gate = gate; _tsdb = tsdb; _httpFactory = httpFactory; _library = library; _log = log; }

    private sealed record Filing(string ShowName, string? Sport, int Year, int Episode, string Title,
        string AiredDate, string? PosterUrl, string? ThumbUrl, string ShowKey, int? EventId);

    /// <summary>Move + enrich the finished file. Event-linked / matched recordings file into the Plex layout;
    /// an unmatched manual recording is parked in a Plex-ignored ".unsorted" folder for a later manual Import.</summary>
    public async Task<string> ImportAsync(int recordingId, string currentPath, CancellationToken ct = default)
    {
        var rec = await _db.Recordings.FindAsync(new object?[] { recordingId }, ct);
        if (rec is null || !File.Exists(currentPath)) return currentPath;

        var f = await BuildFilingAsync(rec, ct);
        if (f is null)
        {
            var staged = await StageUnsortedAsync(recordingId, currentPath, ct); // no event + no match → stage for manual Import
            // Don't let a "Match"-ticked recording fail SILENTLY: tell the user it couldn't be matched and needs a
            // manual Import (only when a match was actually attempted — a plain manual recording expects to be unsorted).
            if (!string.IsNullOrWhiteSpace(rec.MatchQuery))
            {
                try
                {
                    await _gate.WriteAsync(async () =>
                    {
                        _db.Notifications.Add(new Data.Entities.Notification
                        {
                            RecordingId = recordingId, TsUtc = EpochTime.Now(), Kind = NotificationKind.Unmatched,
                            Severity = Severity.Warn,
                            Message = $"couldn't auto-match “{rec.Title}” to a game — it's in the library’s unsorted area; use Import to file it",
                        });
                        await _db.SaveChangesAsync(ct);
                    }, ct);
                }
                catch (Exception ex) { _log.LogDebug(ex, "[Media] unmatched-notification write failed for {Id}", recordingId); }
            }
            return staged;
        }
        return await FileRecordingAsync(rec, currentPath, f, ct);
    }

    /// <summary>Park an unmatched manual recording in a Plex-ignored "<MediaDir>/.unsorted" folder (Plex skips
    /// dot-prefixed dirs on scan), awaiting a manual Import (sport → league → game) from the UI.</summary>
    private async Task<string> StageUnsortedAsync(int recordingId, string currentPath, CancellationToken ct)
    {
        try
        {
            var stageDir = Path.Combine(_paths.MediaDir, ".unsorted");
            var dest = Path.Combine(stageDir, Path.GetFileName(currentPath));
            if (string.Equals(currentPath, dest, StringComparison.OrdinalIgnoreCase)) return currentPath; // already staged
            Directory.CreateDirectory(stageDir);
            // Don't clobber a DIFFERENT recording already staged under the same filename (two same-title/same-minute
            // manual recordings produce identical flat names) — suffix with the unique recording id.
            if (File.Exists(dest))
                dest = Path.Combine(stageDir, $"{Path.GetFileNameWithoutExtension(currentPath)} [{recordingId}]{Path.GetExtension(currentPath)}");
            File.Move(currentPath, dest, overwrite: true);
            await _gate.WriteAsync(async () =>
            {
                var r = await _db.Recordings.FindAsync(recordingId);
                if (r != null) { r.OutputPath = dest; r.UpdatedUtc = EpochTime.Now(); await _db.SaveChangesAsync(ct); }
            }, ct);
            _log.LogInformation("[Media] Recording {Id} staged for manual import → {Path}", recordingId, dest);
            return dest;
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Media] staging failed for {Id}; leaving at {Path}", recordingId, currentPath); return currentPath; }
    }

    /// <summary>Manual import: re-file a staged recording onto a user-chosen TheSportsDB event (sport → league → game).
    /// Returns (ok, newPath, error). Reuses the same filing engine as auto-import.</summary>
    public async Task<(bool ok, string? path, string? error)> AssignAsync(int recordingId, string leagueId, string eventId, CancellationToken ct = default)
    {
        var rec = await _db.Recordings.FindAsync(new object?[] { recordingId }, ct);
        if (rec is null) return (false, null, "recording not found");
        var current = rec.OutputPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) return (false, null, "recording file not found on disk");

        var ev = await _tsdb.GetEventByIdAsync(eventId, ct);
        if (ev is null) return (false, null, "TheSportsDB event not found");
        var resolvedLeagueId = string.IsNullOrWhiteSpace(ev.LeagueId) ? leagueId : ev.LeagueId!;

        // Prefer a LOCAL event. The DVarr Plex agent numbers episodes by a game's position among the LOCAL events
        // for the league+year (PlexEndpoints.SeasonEventsAsync), so for Plex to match the imported file its on-disk
        // SxxExx MUST come from that same ordinal — NOT day-of-year (the old code's E167 bug) or intRound. Map the
        // chosen TheSportsDB game to its local Event (by TheSportsDB id, else the nearest start in the same league),
        // link the recording to it, and re-use the identical event-linked filing path as auto-import.
        var league = await _db.Leagues.FirstOrDefaultAsync(l => l.ExternalLeagueId == resolvedLeagueId, ct);
        if (league is not null)
        {
            var localEv = await FindLocalEventAsync(league.Id, eventId, ev, ct);
            if (localEv is not null)
            {
                // Link the recording to the event so BuildFilingAsync takes the event-linked branch (SeasonOrdinalAsync,
                // == the Plex agent's index) — manual import now produces byte-identical numbering to auto-import.
                await _gate.WriteAsync(async () =>
                {
                    var r = await _db.Recordings.FindAsync(recordingId);
                    if (r is not null) { r.EventId = localEv.Id; r.UpdatedUtc = EpochTime.Now(); await _db.SaveChangesAsync(ct); }
                }, ct);
                rec.EventId = localEv.Id;
                var ef = await BuildFilingAsync(rec, ct);
                if (ef is not null)
                {
                    _log.LogInformation("[Media] Manual import {Rid} linked to event {Eid} ({Title}) → E{Ep:D2}", recordingId, localEv.Id, localEv.Title, ef.Episode);
                    var filed = await FileRecordingAsync(rec, current!, ef, ct);
                    await _library.UpsertForRecordingAsync(recordingId, filed, ct); // moves out of the Unsorted group
                    return (true, filed, null);
                }
            }
        }

        // No local event (league not monitored locally) → the DVarr Plex agent can't serve episodes for it anyway,
        // so the number is cosmetic; still enrich from TheSportsDB and number by chronological season position
        // (stable & sane) rather than day-of-year.
        var lk = await _tsdb.LookupLeagueAsync(resolvedLeagueId, ct);
        var local = EpochTime.ToDisplay(ev.StartUtc ?? rec.StartUtc);
        var ordinal = await TsdbSeasonOrdinalAsync(resolvedLeagueId, ev, local.Year, ct);
        var f = new Filing(
            ev.League ?? lk?.Name ?? "Sports", ev.Sport ?? lk?.Sport, local.Year, ordinal, ev.Title, local.ToString("yyyy-MM-dd"),
            lk?.Poster ?? ev.Poster, ev.Thumb ?? ev.Poster ?? lk?.Poster, $"tsdb-league-{resolvedLeagueId}", null);
        var path = await FileRecordingAsync(rec, current!, f, ct);
        await _library.UpsertForRecordingAsync(recordingId, path, ct); // moves out of the Unsorted group
        return (true, path, null);
    }

    /// <summary>Manual import for a library file with NO Recording row behind it (a scan-adopted file whose
    /// recording was deleted, or media dropped in from outside). Identical filing engine to AssignAsync, driven
    /// by a transient un-persisted Recording carrier (Id=0 → every "persist onto the row" step no-ops safely).
    /// The caller re-points the LibraryItem at the returned path (LibraryService.RefileItemAsync).</summary>
    public async Task<(bool ok, string? path, string? error)> AssignFileAsync(string currentPath, long startUtcHint, string leagueId, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath)) return (false, null, "file not found on disk");

        var ev = await _tsdb.GetEventByIdAsync(eventId, ct);
        if (ev is null) return (false, null, "TheSportsDB event not found");
        var resolvedLeagueId = string.IsNullOrWhiteSpace(ev.LeagueId) ? leagueId : ev.LeagueId!;
        var carrier = new Data.Entities.Recording
        {
            Id = 0, // never persisted: FileRecordingAsync's row update FindAsync(0) misses by design
            StartUtc = startUtcHint > 0 ? startUtcHint : ev.StartUtc ?? EpochTime.Now(),
            OutputPath = currentPath,
            Title = Path.GetFileNameWithoutExtension(currentPath),
        };

        var league = await _db.Leagues.FirstOrDefaultAsync(l => l.ExternalLeagueId == resolvedLeagueId, ct);
        if (league is not null)
        {
            var localEv = await FindLocalEventAsync(league.Id, eventId, ev, ct);
            if (localEv is not null)
            {
                carrier.EventId = localEv.Id;
                var ef = await BuildFilingAsync(carrier, ct);
                if (ef is not null)
                {
                    _log.LogInformation("[Media] Adopted-file import linked to event {Eid} ({Title}) → E{Ep:D2}", localEv.Id, localEv.Title, ef.Episode);
                    return (true, await FileRecordingAsync(carrier, currentPath, ef, ct), null);
                }
            }
        }

        var lk = await _tsdb.LookupLeagueAsync(resolvedLeagueId, ct);
        var local = EpochTime.ToDisplay(ev.StartUtc ?? carrier.StartUtc);
        var ordinal = await TsdbSeasonOrdinalAsync(resolvedLeagueId, ev, local.Year, ct);
        var f = new Filing(
            ev.League ?? lk?.Name ?? "Sports", ev.Sport ?? lk?.Sport, local.Year, ordinal, ev.Title, local.ToString("yyyy-MM-dd"),
            lk?.Poster ?? ev.Poster, ev.Thumb ?? ev.Poster ?? lk?.Poster, $"tsdb-league-{resolvedLeagueId}", null);
        return (true, await FileRecordingAsync(carrier, currentPath, f, ct), null);
    }

    /// <summary>Local Event for a chosen TheSportsDB game: by TheSportsDB id, else the nearest start in the same
    /// league — but ONLY if it also looks like the SAME fixture (tight window + title match), so a doubleheader or
    /// a date-only tie can't silently link the wrong game. Deterministic Id tiebreak for equidistant candidates.</summary>
    private async Task<Data.Entities.Event?> FindLocalEventAsync(int leagueId, string tsdbEventId, TsdbEvent ev, CancellationToken ct)
    {
        var localEv = await _db.Events.FirstOrDefaultAsync(e => e.LeagueId == leagueId && e.TsdbEventId == tsdbEventId, ct);
        if (localEv is null && ev.StartUtc is { } su)
        {
            var cands = await _db.Events.Where(e => e.LeagueId == leagueId)
                .Select(e => new { e.Id, e.StartUtc, e.Title }).ToListAsync(ct);
            var near = cands.OrderBy(e => Math.Abs(e.StartUtc - su)).ThenBy(e => e.Id).FirstOrDefault();
            if (near is not null && Math.Abs(near.StartUtc - su) <= 30 * 60 && TitlesSimilar(near.Title, ev.Title))
                localEv = await _db.Events.FindAsync(new object?[] { near.Id }, ct);
        }
        return localEv;
    }

    /// <summary>Chronological 1-based position of a TheSportsDB event within its season's events (the manual-import
    /// fallback used only when the league isn't local). Falls back to intRound, then day-of-year, if the season list
    /// can't be read.</summary>
    private async Task<int> TsdbSeasonOrdinalAsync(string leagueId, TsdbEvent ev, int year, CancellationToken ct)
    {
        try
        {
            var season = string.IsNullOrWhiteSpace(ev.Season) ? year.ToString() : ev.Season!;
            var all = await _tsdb.GetSeasonEventsAsync(leagueId, season, ct);
            var ordered = all.Where(e => e.StartUtc is not null)
                .OrderBy(e => e.StartUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            var idx = ordered.FindIndex(e => e.Id == ev.Id);
            if (idx >= 0) return idx + 1;
        }
        catch (Exception ex) { _log.LogDebug(ex, "[Media] TheSportsDB season-ordinal lookup failed for league {Lid}", leagueId); }
        return ev.Round is > 0 ? ev.Round!.Value : EpochTime.ToDisplay(ev.StartUtc ?? 0L).DayOfYear;
    }

    /// <summary>Move + enrich the file into the Plex/Jellyfin per-game layout from a resolved Filing.
    /// Shared by auto-import (event/match) and manual assignment.</summary>
    private async Task<string> FileRecordingAsync(Data.Entities.Recording rec, string currentPath, Filing f, CancellationToken ct)
    {
        var recordingId = rec.Id;
        // Resolution tag (HDTV-<height>p, e.g. HDTV-2160p) probed from the finished file BEFORE the move.
        var resTag = await ProbeResolutionTagAsync(currentPath, ct);

        var showName = Sanitize(f.ShowName);
        var epTag = $"E{f.Episode:D2}";
        // Sonarr/Plex-style per-game folder: "<Title> (yyyy-MM-dd) E<NN>", with the video + sidecars inside it.
        var gameFolderName = Sanitize($"{f.Title} ({f.AiredDate}) {epTag}");
        var baseName = Sanitize($"{showName} - S{f.Year}{epTag} - {f.Title}" + (resTag is null ? "" : $" - {resTag}"));
        var showFolder = Path.Combine(_paths.MediaDir, showName);
        var seasonFolder = Path.Combine(showFolder, $"Season {f.Year}");
        var gameFolder = Path.Combine(seasonFolder, gameFolderName);
        var finalMkv = Path.Combine(gameFolder, baseName + ".mkv");

        // The per-game folder (date + episode + title) is already collision-resistant, but never clobber a DIFFERENT
        // recording that resolved to the same path; a re-finalize of THIS recording (OutputPath == finalMkv) overwrites.
        if (File.Exists(finalMkv) && !string.Equals(rec.OutputPath, finalMkv, StringComparison.OrdinalIgnoreCase))
        {
            baseName = Sanitize($"{baseName} [{recordingId}]");
            finalMkv = Path.Combine(gameFolder, baseName + ".mkv");
        }

        // Defensive containment: a provider-derived show/title can never escape the media root.
        var rootFull = Path.GetFullPath(_paths.MediaDir);
        if (!Path.GetFullPath(finalMkv).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("[Media] Refusing import outside media root for recording {Id}; leaving file at {Path}", recordingId, currentPath);
            return currentPath;
        }

        try
        {
            Directory.CreateDirectory(gameFolder);
            if (!string.Equals(currentPath, finalMkv, StringComparison.OrdinalIgnoreCase))
                File.Move(currentPath, finalMkv, overwrite: true);

            // Persist the real location IMMEDIATELY — sidecars/artwork are best-effort; DONE must be truthful.
            await _gate.WriteAsync(async () =>
            {
                var r = await _db.Recordings.FindAsync(recordingId);
                if (r != null) { r.OutputPath = finalMkv; r.UpdatedUtc = EpochTime.Now(); await _db.SaveChangesAsync(ct); }
            }, ct);

            try
            {
                // Show-level artwork/metadata stay at the show root (Plex convention); episode .nfo + thumb live next
                // to the video inside the per-game folder.
                await WriteShowNfoAsync(showFolder, f.ShowKey, showName, f.Sport, ct);
                await WriteEpisodeNfoAsync(Path.Combine(gameFolder, baseName + ".nfo"), f, ct);

                await DownloadImageAsync(f.PosterUrl, Path.Combine(showFolder, "poster.jpg"), ct);
                await DownloadImageAsync(f.PosterUrl, Path.Combine(showFolder, $"Season{f.Year}.jpg"), ct);

                var epThumb = Path.Combine(gameFolder, baseName + ".jpg"); // matches video basename (stock convention)
                if (!await DownloadImageAsync(f.ThumbUrl, epThumb, ct))
                    await GenerateThumbnailAsync(finalMkv, epThumb, ct); // fall back to a frame grab
            }
            catch (Exception ex) { _log.LogWarning(ex, "[Media] sidecar/artwork enrichment failed for {Id} (file is safe at {Path})", recordingId, finalMkv); }

            _log.LogInformation("[Media] Imported recording {Id} → {Path}", recordingId, finalMkv);
            return finalMkv;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Media] Import failed for recording {Id}", recordingId);
            return File.Exists(finalMkv) ? finalMkv : currentPath;
        }
    }

    /// <summary>Resolve where/how to file this recording: from its linked DVarr Event, else a TheSportsDB match query, else null (flat).</summary>
    private async Task<Filing?> BuildFilingAsync(Data.Entities.Recording rec, CancellationToken ct)
    {
        // 1) Event-linked (auto-scheduled): authoritative, and its ordinal matches the Plex provider's episode index.
        if (rec.EventId is { } eid)
        {
            var f = await FilingForEventAsync(eid, ct);
            if (f is not null) return f;
        }

        // 2) Local-event match (no EventId): bind to a LOCAL synced event this recording COVERS, before the fragile
        // TheSportsDB text search. DVarr syncs the full season of every monitored league, so a monitored game is local.
        if (rec.EventId is null)
        {
            try
            {
                var winStart = rec.StartUtc - rec.PrePadS;
                var winEnd = rec.EndUtc + rec.PostPadS;

                // 2a) Team-token match on the recording's match query, falling back to its title. Matching on TEAM
                // tokens (not the whole title) is robust for foreign-language guide titles — "FC Porto"/"Moreirense"
                // appear in both the Polish EPG title and the TheSportsDB event, while league-label noise ("Liga
                // portugalska", "mecz:") doesn't overlap and is ignored. The title fallback catches a recording whose
                // schedule set no explicit match query but whose title still carries the teams.
                var matchText = !string.IsNullOrWhiteSpace(rec.MatchQuery) ? rec.MatchQuery : rec.Title;
                var qTokens = MatchTokens(matchText);
                if (qTokens.Count > 0)
                {
                    var candidates = await _db.Events.Where(e => e.StartUtc >= winStart && e.StartUtc <= winEnd)
                        .Select(e => new { e.Id, e.Title, e.StartUtc }).ToListAsync(ct);
                    var best = candidates
                        .Select(e => new { e, score = qTokens.Intersect(MatchTokens(e.Title)).Count() })
                        .Where(x => x.score >= 2) // ≥2 shared significant tokens as the ranking floor
                        // Two shared tokens can BOTH belong to one side — "Manchester" + "United" matches
                        // Manchester United vs ANY opponent (audit MEDIA-01). For a two-sided event title each
                        // side must be evidenced by its own unique tokens; single-name events keep the ≥2 rule.
                        .Where(x =>
                        {
                            var (a, b) = ResolverService.EventSides(x.e.Title);
                            return ReferenceEquals(a, b) || ResolverService.ShowsBothTeams(matchText, a, b);
                        })
                        .OrderByDescending(x => x.score).ThenBy(x => Math.Abs(x.e.StartUtc - rec.StartUtc))
                        .FirstOrDefault();
                    if (best is not null)
                    {
                        var f = await FilingForEventAsync(best.e.Id, ct, $"by team tokens (score {best.score})", rec.Id);
                        if (f is not null) return f;
                    }
                }

                // 2b) Channel→league→event: a recording on a channel MAPPED to a monitored league, with exactly one of
                // that league's events airing in the window, files against that event even when the guide title is
                // generic ("MLB Baseball") and carries no team names — the mapping + time is signal enough. Fixes a
                // manual/guide MLB recording that landed unsorted because its EPG title had no team names to match on.
                var leagueIds = await _db.LeagueChannelMaps.Where(m => m.ChannelId == rec.ChannelId)
                    .Select(m => m.LeagueId).Distinct().ToListAsync(ct);
                if (leagueIds.Count > 0)
                {
                    var evIds = await _db.Events.Where(e => leagueIds.Contains(e.LeagueId) && e.Monitored
                            && e.StartUtc >= winStart && e.StartUtc <= winEnd)
                        .Select(e => e.Id).ToListAsync(ct);
                    if (evIds.Count == 1) // only when unambiguous — never guess between two games in the window
                    {
                        var f = await FilingForEventAsync(evIds[0], ct, "by mapped channel + time window", rec.Id);
                        if (f is not null) return f;
                    }
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "[Media] local-event match failed for recording {Id}", rec.Id); }
        }

        // 3) Manual recording with a TheSportsDB match query: best-effort text search for a Plex-clean name.
        if (!string.IsNullOrWhiteSpace(rec.MatchQuery))
        {
            try
            {
                var year = EpochTime.ToDisplay(rec.StartUtc).Year;
                var hits = await _tsdb.SearchEventsAsync(rec.MatchQuery!, year.ToString(), ct);
                if (hits.Count == 0) hits = await _tsdb.SearchEventsAsync(rec.MatchQuery!, null, ct);
                // The raw EPG title often carries league-label noise around the teams ("Liga portugalska - mecz: …"),
                // which sinks TheSportsDB's event-name search. If it missed, retry with just the significant tokens
                // (team names), which searches far more reliably.
                if (hits.Count == 0)
                {
                    var cleaned = string.Join(" ", MatchTokens(rec.MatchQuery!));
                    if (cleaned.Length > 0 && !string.Equals(cleaned, rec.MatchQuery!.Trim(), StringComparison.OrdinalIgnoreCase))
                        hits = await _tsdb.SearchEventsAsync(cleaned, null, ct);
                }
                var best = PickBestMatch(hits, rec.StartUtc);
                if (best is not null)
                {
                    var local = EpochTime.ToDisplay(best.StartUtc ?? rec.StartUtc);
                    var localYear = local.Year;
                    var poster = best.Poster;
                    if (!string.IsNullOrWhiteSpace(best.LeagueId))
                    {
                        var lk = await _tsdb.LookupLeagueAsync(best.LeagueId!, ct);
                        poster = lk?.Poster ?? poster;
                    }
                    // NOT intRound: TheSportsDB gives every group-stage match the same round number (all matchday-1
                    // World Cup games are intRound=1), which collapsed them all to E01. A manual recording has no local
                    // Event row to take a tournament ordinal from, so use the day-of-year (unique per day; the per-game
                    // folder also carries the full date + title, so same-day games never overwrite). Monitor an event
                    // instead of manual-scheduling it to get the clean chronological E-number (the event-linked path).
                    var ep = local.DayOfYear;
                    return new Filing(best.League ?? rec.MatchQuery!, best.Sport, localYear, ep, best.Title, local.ToString("yyyy-MM-dd"),
                        poster, best.Thumb ?? best.Poster, $"tsdb-event-{best.Id}", null);
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "[Media] TheSportsDB match failed for recording {Id}", rec.Id); }
        }
        return null;
    }

    /// <summary>Build a Filing from a linked DVarr Event (shared by the event-linked and local-match tiers). Returns
    /// null when the event/league row is gone; logs the match reason when supplied.</summary>
    private async Task<Filing?> FilingForEventAsync(int eventId, CancellationToken ct, string? matchReason = null, int? recId = null)
    {
        var ev = await _db.Events.FindAsync(new object?[] { eventId }, ct);
        var league = ev is null ? null : await _db.Leagues.FindAsync(new object?[] { ev.LeagueId }, ct);
        if (ev is null || league is null) return null;
        if (matchReason is not null)
            _log.LogInformation("[Media] Recording {Id} matched local event {Eid} '{Title}' {Why}", recId, ev.Id, ev.Title, matchReason);
        var local = EpochTime.ToDisplay(ev.StartUtc);
        var year = local.Year;
        var ep = await SeasonOrdinalAsync(league.Id, year, ev.Id, ev.StartUtc, ct);
        return new Filing(league.Name, league.Sport, year, ep, ev.Title, local.ToString("yyyy-MM-dd"),
            league.PosterUrl, string.IsNullOrWhiteSpace(ev.ThumbUrl) ? league.PosterUrl : ev.ThumbUrl,
            $"league-{league.Id}", ev.Id);
    }

    private async Task<int> SeasonOrdinalAsync(int leagueId, int year, int eventId, long eventStartUtc, CancellationToken ct)
    {
        // ThenBy(Id) gives a STABLE tie-break (motorsport sessions / date-only events can share StartUtc) so this
        // ordinal exactly matches PlexEndpoints' episode index — without it Plex wouldn't match DVarr's own files.
        var ordered = await _db.Events.Where(e => e.LeagueId == leagueId).OrderBy(e => e.StartUtc).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.StartUtc }).ToListAsync(ct);
        var inYear = ordered.Where(e => EpochTime.ToDisplay(e.StartUtc).Year == year).ToList();
        var idx = inYear.FindIndex(e => e.Id == eventId);
        if (idx >= 0) return idx + 1;
        // The event is always expected in its own league+year set, so this only fires if the row was deleted/re-keyed
        // between scheduling and finalize. Fall back to day-of-year (unique per day) rather than a silent E01 that
        // would collide with the real first event.
        _log.LogWarning("[Media] event {Eid} absent from league {Lid} year {Yr} ordinal set; using date fallback", eventId, leagueId, year);
        return EpochTime.ToDisplay(eventStartUtc).DayOfYear;
    }

    // Common connective / broadcast noise that must never count as a "team" token when matching a recording title to an
    // event (kept tiny + language-agnostic; league-label words are naturally ignored since they don't appear in the
    // TheSportsDB event title). Everything else of length >= 3 is treated as significant (team names, cities).
    private static readonly HashSet<string> MatchStop = new(StringComparer.Ordinal)
    { "vs", "the", "and", "live", "match", "game", "fixture", "round", "matchday" };

    /// <summary>Significant lowercased alphanumeric tokens (length >= 3, minus obvious connective noise) of a title —
    /// the basis for team-name overlap matching. "FC Porto - Moreirense FC" → {porto, moreirense}.</summary>
    internal static HashSet<string> MatchTokens(string? s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(s)) return set;
        foreach (var raw in System.Text.RegularExpressions.Regex.Split(s!.ToLowerInvariant(), "[^a-z0-9]+"))
            if (raw.Length >= 3 && !MatchStop.Contains(raw)) set.Add(raw);
        return set;
    }

    private static TsdbEvent? PickBestMatch(List<TsdbEvent> hits, long startUtc)
    {
        if (hits.Count == 0) return null;
        // Prefer the event whose start is closest to the recording (within ~2 days); else the first hit.
        var dated = hits.Where(h => h.StartUtc is not null)
            .OrderBy(h => Math.Abs(h.StartUtc!.Value - startUtc)).ToList();
        if (dated.Count > 0 && Math.Abs(dated[0].StartUtc!.Value - startUtc) <= 2 * 86400) return dated[0];
        return hits[0];
    }

    private async Task<bool> DownloadImageAsync(string? url, string destPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0) return true; // idempotent
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var bytes = await http.GetByteArrayAsync(url, ct);
            if (bytes.Length == 0) return false;
            await File.WriteAllBytesAsync(destPath, bytes, ct);
            return true;
        }
        catch (Exception ex) { _log.LogDebug(ex, "[Media] artwork download failed: {Url}", url); return false; }
    }

    private static async Task WriteShowNfoAsync(string showFolder, string showKey, string showName, string? sport, CancellationToken ct)
    {
        var path = Path.Combine(showFolder, "tvshow.nfo");
        if (File.Exists(path)) return; // write once
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<tvshow>");
        sb.AppendLine($"  <title>{Xml(showName)}</title>");
        if (!string.IsNullOrWhiteSpace(sport)) sb.AppendLine($"  <genre>{Xml(sport!)}</genre>");
        sb.AppendLine($"  <uniqueid type=\"dvarr\" default=\"true\">{Xml(showKey)}</uniqueid>");
        sb.AppendLine("  <studio>DVarr</studio>");
        sb.AppendLine("</tvshow>");
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private static async Task WriteEpisodeNfoAsync(string path, Filing f, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<episodedetails>");
        sb.AppendLine($"  <title>{Xml(f.Title)}</title>");
        sb.AppendLine($"  <season>{f.Year}</season>");
        sb.AppendLine($"  <episode>{f.Episode}</episode>");
        // Include the season/year so the same E-number under two different seasons can't collide on one uniqueid
        // (e.g. S2026E01 and S2027E01) — Plex/Kodi treat uniqueid as globally unique within the agent.
        sb.AppendLine($"  <uniqueid type=\"dvarr\" default=\"true\">{Xml(f.ShowKey)}-{f.Year}-{f.Episode}</uniqueid>");
        sb.AppendLine("</episodedetails>");
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private async Task GenerateThumbnailAsync(string mkv, string jpg, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg.Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", "120", "-i", mkv, "-frames:v", "1", "-vf", "scale=640:-1", "-q:v", "3", jpg })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync() is not null) { } } catch { } });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { if (!p.HasExited) p.Kill(true); } catch { } } // never orphan a stuck ffmpeg
            if (!File.Exists(jpg))
            {
                var psi2 = new ProcessStartInfo(_ffmpeg.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", mkv, "-frames:v", "1", "-vf", "scale=640:-1", "-q:v", "3", jpg })
                    psi2.ArgumentList.Add(a);
                using var p2 = Process.Start(psi2)!;
                _ = Task.Run(async () => { try { while (await p2.StandardError.ReadLineAsync() is not null) { } } catch { } });
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(TimeSpan.FromSeconds(30));
                try { await p2.WaitForExitAsync(cts2.Token); } catch (OperationCanceledException) { try { if (!p2.HasExited) p2.Kill(true); } catch { } }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Media] thumbnail generation failed for {Mkv}", mkv); }
    }

    /// <summary>ffprobe the video height of the finished file → a Sonarr/Plex-style "HDTV-&lt;height&gt;p" tag
    /// (e.g. HDTV-2160p, HDTV-1080p). Returns null if it can't be read (the file is still filed, just without a tag).</summary>
    private async Task<string?> ProbeResolutionTagAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg.Ffprobe)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "quiet", "-select_streams", "v:0", "-show_entries", "stream=height", "-of", "default=nk=1:nw=1", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            string outp;
            try { outp = await p.StandardOutput.ReadToEndAsync(cts.Token); await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { if (!p.HasExited) p.Kill(true); } catch { } return null; } // bound a hung ffprobe + don't orphan it
            var line = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return int.TryParse(line, out var h) && h > 0 ? $"HDTV-{h}p" : null;
        }
        catch (Exception ex) { _log.LogDebug(ex, "[Media] resolution probe failed for {Path}", path); return null; }
    }

    /// <summary>Loose title equivalence (alphanumeric, case-insensitive, substring either way) — guards the manual-import
    /// nearest-start fallback so it cannot link to a different fixture that merely starts near the same time.</summary>
    private static bool TitlesSimilar(string? a, string? b)
    {
        static string N(string? s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var na = N(a); var nb = N(b);
        return na.Length > 0 && nb.Length > 0 && (na.Contains(nb) || nb.Contains(na));
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Join("_", s.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) return "Sports";
        return cleaned;
    }

    private static string Xml(string s)
    {
        var clean = new string(s.Where(c => c == '\t' || c == '\n' || c == '\r' || c >= ' ').ToArray());
        return clean.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
