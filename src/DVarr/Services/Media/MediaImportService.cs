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
    private readonly ILogger<MediaImportService> _log;

    public MediaImportService(DVarrDbContext db, FfmpegLocator ffmpeg, RuntimePaths paths, DbWriteGate gate,
        TheSportsDbClient tsdb, IHttpClientFactory httpFactory, ILogger<MediaImportService> log)
    { _db = db; _ffmpeg = ffmpeg; _paths = paths; _gate = gate; _tsdb = tsdb; _httpFactory = httpFactory; _log = log; }

    private sealed record Filing(string ShowName, string? Sport, int Year, int Episode, string Title,
        string AiredDate, string? PosterUrl, string? ThumbUrl, string ShowKey, int? EventId);

    /// <summary>Move + enrich the finished file. Returns the final path (== input if not filed).</summary>
    public async Task<string> ImportAsync(int recordingId, string currentPath, CancellationToken ct = default)
    {
        var rec = await _db.Recordings.FindAsync(new object?[] { recordingId }, ct);
        if (rec is null || !File.Exists(currentPath)) return currentPath;

        var f = await BuildFilingAsync(rec, ct);
        if (f is null) return currentPath; // no event + no match → leave flat

        // Resolution tag (HDTV-<height>p, e.g. HDTV-2160p) probed from the finished file BEFORE the move.
        var resTag = await ProbeResolutionTagAsync(currentPath, ct);

        var showName = Sanitize(f.ShowName);
        var epTag = $"E{f.Episode:D2}";
        // Sportarr-style per-game folder: "<Title> (yyyy-MM-dd) E<NN>", with the video + sidecars inside it.
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
            var ev = await _db.Events.FindAsync(new object?[] { eid }, ct);
            var league = ev is null ? null : await _db.Leagues.FindAsync(new object?[] { ev.LeagueId }, ct);
            if (ev is not null && league is not null)
            {
                var bne = EpochTime.ToBrisbane(ev.StartUtc);
                var year = bne.Year;
                var ep = await SeasonOrdinalAsync(league.Id, year, ev.Id, ev.StartUtc, ct);
                return new Filing(league.Name, league.Sport, year, ep, ev.Title, bne.ToString("yyyy-MM-dd"),
                    league.PosterUrl, string.IsNullOrWhiteSpace(ev.ThumbUrl) ? league.PosterUrl : ev.ThumbUrl,
                    $"league-{league.Id}", ev.Id);
            }
        }

        // 2) Manual recording with a TheSportsDB match query: best-effort enrich for a Plex-clean name.
        if (!string.IsNullOrWhiteSpace(rec.MatchQuery))
        {
            try
            {
                var year = EpochTime.ToBrisbane(rec.StartUtc).Year;
                var hits = await _tsdb.SearchEventsAsync(rec.MatchQuery!, year.ToString(), ct);
                if (hits.Count == 0) hits = await _tsdb.SearchEventsAsync(rec.MatchQuery!, null, ct);
                var best = PickBestMatch(hits, rec.StartUtc);
                if (best is not null)
                {
                    var bne = EpochTime.ToBrisbane(best.StartUtc ?? rec.StartUtc);
                    var bneYear = bne.Year;
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
                    var ep = bne.DayOfYear;
                    return new Filing(best.League ?? rec.MatchQuery!, best.Sport, bneYear, ep, best.Title, bne.ToString("yyyy-MM-dd"),
                        poster, best.Thumb ?? best.Poster, $"tsdb-event-{best.Id}", null);
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "[Media] TheSportsDB match failed for recording {Id}", rec.Id); }
        }
        return null;
    }

    private async Task<int> SeasonOrdinalAsync(int leagueId, int year, int eventId, long eventStartUtc, CancellationToken ct)
    {
        // ThenBy(Id) gives a STABLE tie-break (motorsport sessions / date-only events can share StartUtc) so this
        // ordinal exactly matches PlexEndpoints' episode index — without it Plex wouldn't match DVarr's own files.
        var ordered = await _db.Events.Where(e => e.LeagueId == leagueId).OrderBy(e => e.StartUtc).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.StartUtc }).ToListAsync(ct);
        var inYear = ordered.Where(e => EpochTime.ToBrisbane(e.StartUtc).Year == year).ToList();
        var idx = inYear.FindIndex(e => e.Id == eventId);
        if (idx >= 0) return idx + 1;
        // The event is always expected in its own league+year set, so this only fires if the row was deleted/re-keyed
        // between scheduling and finalize. Fall back to day-of-year (unique per day) rather than a silent E01 that
        // would collide with the real first event.
        _log.LogWarning("[Media] event {Eid} absent from league {Lid} year {Yr} ordinal set; using date fallback", eventId, leagueId, year);
        return EpochTime.ToBrisbane(eventStartUtc).DayOfYear;
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
        sb.AppendLine($"  <uniqueid type=\"dvarr\" default=\"true\">{Xml(f.ShowKey)}-{f.Episode}</uniqueid>");
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
            await p.WaitForExitAsync(cts.Token);
            if (!File.Exists(jpg))
            {
                var psi2 = new ProcessStartInfo(_ffmpeg.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", mkv, "-frames:v", "1", "-vf", "scale=640:-1", "-q:v", "3", jpg })
                    psi2.ArgumentList.Add(a);
                using var p2 = Process.Start(psi2)!;
                _ = Task.Run(async () => { try { while (await p2.StandardError.ReadLineAsync() is not null) { } } catch { } });
                await p2.WaitForExitAsync(ct);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Media] thumbnail generation failed for {Mkv}", mkv); }
    }

    /// <summary>ffprobe the video height of the finished file → a Sportarr-style "HDTV-&lt;height&gt;p" tag
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
            var outp = await p.StandardOutput.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync(cts.Token);
            var line = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return int.TryParse(line, out var h) && h > 0 ? $"HDTV-{h}p" : null;
        }
        catch (Exception ex) { _log.LogDebug(ex, "[Media] resolution probe failed for {Path}", path); return null; }
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
