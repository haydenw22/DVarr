using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Media;

/// <summary>Technical metadata read from a video file with ffprobe (all-null when ffprobe is unavailable —
/// the item is still tracked, just without codec/duration detail).</summary>
public sealed record MediaInfo(int? DurationS, string? VideoCodec, string? AudioCodec, int? Height);

/// <summary>What the on-disk location of a file says about it, parsed from DVarr's own Plex layout
/// ("Show/Season YYYY/Title (yyyy-MM-dd) ENN/Show - SYYYYENN - Title - HDTV-1080p.mkv").</summary>
public sealed record ParsedPath(string? ShowName, int SeasonYear, int EpisodeNum, string Title, long? StartUtc, int? Height, bool Unsorted);

/// <summary>
/// The library's single write path: turns a physical video file into (or refreshes) its LibraryItem row.
/// Called by the recorder at finalize, by manual import when a file is re-filed, and by the reconciling
/// disk scan when it adopts a file DVarr didn't create. Also owns the shared ffprobe / path-parse helpers
/// and the contained file-delete used by the Library page.
/// </summary>
public sealed class LibraryService
{
    private readonly DVarrDbContext _db;
    private readonly DbWriteGate _gate;
    private readonly RuntimePaths _paths;
    private readonly FfmpegLocator _ffmpeg;
    private readonly ILogger<LibraryService> _log;

    public LibraryService(DVarrDbContext db, DbWriteGate gate, RuntimePaths paths, FfmpegLocator ffmpeg, ILogger<LibraryService> log)
    { _db = db; _gate = gate; _paths = paths; _ffmpeg = ffmpeg; _log = log; }

    // =====================================================================
    // Upsert — the one way library rows come into existence
    // =====================================================================

    /// <summary>
    /// Create or refresh the library row for a recording's finished file. Metadata preference order:
    /// linked Event/League (authoritative) → the on-disk path DVarr itself wrote → the recording row.
    /// Never throws — a library bookkeeping failure must not fail a finalize.
    /// </summary>
    public async Task UpsertForRecordingAsync(int recordingId, string filePath, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
            var rec = await _db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordingId, ct);
            var ev = rec?.EventId is { } eid ? await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid, ct) : null;
            var league = ev is null ? null : await _db.Leagues.AsNoTracking().FirstOrDefaultAsync(l => l.Id == ev.LeagueId, ct);
            var chName = rec is null ? null : await _db.Channels.Where(c => c.Id == rec.ChannelId).Select(c => (string?)c.Name).FirstOrDefaultAsync(ct);
            var srcLabel = rec is null ? null : await _db.Sources.Where(s => s.Id == rec.SourceId).Select(s => (string?)s.Label).FirstOrDefaultAsync(ct);

            var parsed = ParsePath(filePath);
            var probe = await ProbeAsync(filePath, ct);
            var now = EpochTime.Now();

            var item = new LibraryItem
            {
                RecordingId = recordingId,
                EventId = ev?.Id,
                LeagueId = league?.Id,
                ShowName = league?.Name ?? parsed.ShowName ?? "",
                Sport = league?.Sport,
                SeasonYear = parsed.SeasonYear != 0 ? parsed.SeasonYear
                    : ev is not null ? EpochTime.ToDisplay(ev.StartUtc).Year
                    : rec is not null ? EpochTime.ToDisplay(rec.StartUtc).Year : 0,
                EpisodeNum = parsed.EpisodeNum,
                Title = ev?.Title ?? (string.IsNullOrWhiteSpace(rec?.Title) ? parsed.Title : rec!.Title!),
                StartUtc = ev?.StartUtc ?? rec?.StartUtc ?? parsed.StartUtc ?? now,
                FilePath = filePath,
                FileBytes = SafeLength(filePath),
                DurationS = probe.DurationS,
                VideoCodec = probe.VideoCodec,
                AudioCodec = probe.AudioCodec,
                Height = probe.Height ?? parsed.Height,
                ChannelName = chName,
                SourceLabel = srcLabel,
                Origin = LibraryItemOrigin.Recorded,
                Status = LibraryItemStatus.Ok,
                Unsorted = parsed.Unsorted,
            };
            await UpsertAsync(item, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Library] upsert for recording {Id} failed (file is safe at {Path})", recordingId, filePath);
        }
    }

    /// <summary>Insert-or-update by RecordingId (a re-file/re-finalize moves the same recording's file), else by
    /// FilePath (scan adoption / a file another row already tracks). Serialized through the write gate.</summary>
    public async Task<LibraryItem> UpsertAsync(LibraryItem incoming, CancellationToken ct = default)
    {
        LibraryItem result = incoming;
        await _gate.WriteAsync(async () =>
        {
            var now = EpochTime.Now();
            var byRec = incoming.RecordingId is { } rid
                ? await _db.LibraryItems.FirstOrDefaultAsync(i => i.RecordingId == rid, ct) : null;
            var byPath = await _db.LibraryItems.FirstOrDefaultAsync(i => i.FilePath == incoming.FilePath, ct);
            // Same physical file tracked twice (a scan adopted it in the window before finalize registered it):
            // keep the recording-linked row, absorb the adopted one — the unique FilePath index forbids both.
            // Saved separately BEFORE the update below: EF doesn't order same-table delete-vs-update around a
            // unique index, so updating the survivor onto the path first would trip the constraint.
            if (byRec is not null && byPath is not null && byRec.Id != byPath.Id)
            {
                _db.LibraryItems.Remove(byPath);
                await _db.SaveChangesAsync(ct);
            }
            var row = byRec ?? byPath;

            if (row is null)
            {
                incoming.CreatedUtc = now;
                incoming.UpdatedUtc = now;
                _db.LibraryItems.Add(incoming);
                result = incoming;
            }
            else
            {
                row.RecordingId = incoming.RecordingId ?? row.RecordingId;
                row.EventId = incoming.EventId ?? row.EventId;
                row.LeagueId = incoming.LeagueId ?? row.LeagueId;
                if (!string.IsNullOrWhiteSpace(incoming.ShowName)) row.ShowName = incoming.ShowName;
                row.Sport = incoming.Sport ?? row.Sport;
                if (incoming.SeasonYear != 0) row.SeasonYear = incoming.SeasonYear;
                if (incoming.EpisodeNum != 0) row.EpisodeNum = incoming.EpisodeNum;
                if (!string.IsNullOrWhiteSpace(incoming.Title)) row.Title = incoming.Title;
                if (incoming.StartUtc != 0) row.StartUtc = incoming.StartUtc;
                row.FilePath = incoming.FilePath;
                row.FileBytes = incoming.FileBytes;
                row.DurationS = incoming.DurationS ?? row.DurationS;
                row.VideoCodec = incoming.VideoCodec ?? row.VideoCodec;
                row.AudioCodec = incoming.AudioCodec ?? row.AudioCodec;
                row.Height = incoming.Height ?? row.Height;
                row.ChannelName = incoming.ChannelName ?? row.ChannelName;
                row.SourceLabel = incoming.SourceLabel ?? row.SourceLabel;
                // Recorded is stickier than Adopted: a scan re-seeing a recorded file must not demote it.
                if (incoming.Origin == LibraryItemOrigin.Recorded) row.Origin = LibraryItemOrigin.Recorded;
                row.Status = LibraryItemStatus.Ok;   // we just saw the file
                row.MissingSinceUtc = null;
                row.Unsorted = incoming.Unsorted;
                row.UpdatedUtc = now;
                result = row;
            }
            await _db.SaveChangesAsync(ct);
        }, ct);
        return result;
    }

    // =====================================================================
    // ffprobe
    // =====================================================================

    /// <summary>Duration/codec/height via ffprobe JSON. All-null (never a throw) when ffprobe is missing or the
    /// file is unreadable — library tracking must not depend on a working probe.</summary>
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg.Ffprobe)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync() is not null) { } } catch { } });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            string outp;
            try { outp = await p.StandardOutput.ReadToEndAsync(cts.Token); await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { if (!p.HasExited) p.Kill(true); } catch { } return new MediaInfo(null, null, null, null); }

            using var doc = JsonDocument.Parse(outp);
            int? durationS = null; string? v = null, a2 = null; int? height = null;
            if (doc.RootElement.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ds))
                durationS = (int)Math.Round(ds);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    if (type == "video" && v is null && codec is not null and not "mjpeg" and not "png") // skip attached pictures
                    {
                        v = codec;
                        if (s.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) && hv > 0) height = hv;
                    }
                    else if (type == "audio" && a2 is null) a2 = codec;
                }
            return new MediaInfo(durationS, v, a2, height);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[Library] ffprobe failed for {Path}", path);
            return new MediaInfo(null, null, null, null);
        }
    }

    // =====================================================================
    // Path parsing (DVarr's own import layout — see MediaImportService.FileRecordingAsync)
    // =====================================================================

    private static readonly Regex FileRx = new(
        @"^(?<show>.+?) - S(?<yr>\d{4})E(?<ep>\d{1,4}) - (?<title>.+?)(?: - HDTV-(?<h>\d{3,4})p)?(?: \[(?<rid>\d+)\])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GameFolderRx = new(
        @"^(?<title>.+) \((?<date>\d{4}-\d{2}-\d{2})\) E(?<ep>\d{1,4})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeasonFolderRx = new(
        @"^Season (?<yr>\d{4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Reconstruct library metadata from a file's location under the media root. Handles the full
    /// "Show/Season YYYY/Game folder/file" layout, the ".unsorted" staging folder, and flat root files.</summary>
    public ParsedPath ParsePath(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        string? rel = null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.MediaDir));
            var full = Path.GetFullPath(filePath);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                rel = full[(root.Length + 1)..];
        }
        catch { /* fall through to filename-only parsing */ }

        var parts = rel?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? new[] { Path.GetFileName(filePath) };
        var unsorted = parts.Length <= 1 || string.Equals(parts[0], ".unsorted", StringComparison.OrdinalIgnoreCase);

        string? show = null; var year = 0; var ep = 0; var title = baseName; long? start = null; int? height = null;

        // Filename first — it carries the most fields when DVarr named it.
        var fm = FileRx.Match(baseName);
        if (fm.Success)
        {
            show = fm.Groups["show"].Value;
            year = int.Parse(fm.Groups["yr"].Value);
            ep = int.Parse(fm.Groups["ep"].Value);
            title = fm.Groups["title"].Value;
            if (fm.Groups["h"].Success) height = int.Parse(fm.Groups["h"].Value);
        }

        if (!unsorted)
        {
            // Folder layout: <Show>/Season <YYYY>/<Title (yyyy-MM-dd) ENN>/<file> — folders win for show/season
            // (a hand-renamed file inside the right folder still groups correctly).
            show = parts.Length >= 2 ? parts[0] : show;
            if (parts.Length >= 3 && SeasonFolderRx.Match(parts[1]) is { Success: true } sm)
                year = int.Parse(sm.Groups["yr"].Value);
            if (parts.Length >= 4 && GameFolderRx.Match(parts[2]) is { Success: true } gm)
            {
                if (!fm.Success) { title = gm.Groups["title"].Value; ep = int.Parse(gm.Groups["ep"].Value); }
                var dp = gm.Groups["date"].Value.Split('-');
                start = EpochTime.DisplayMidnightUtc(int.Parse(dp[0]), int.Parse(dp[1]), int.Parse(dp[2]));
            }
        }

        // Strip the working-file noise from a flat/staged capture name: "Title [2026-07-12_1400] [#123]".
        if (!fm.Success)
        {
            var m = Regex.Match(title, @"^(?<t>.+?)\s*\[(?<stamp>\d{4}-\d{2}-\d{2}_\d{4})\](?:\s*\[#?\d+\])?$");
            if (m.Success)
            {
                title = m.Groups["t"].Value.Trim();
                var st = m.Groups["stamp"].Value; // display-zone stamp from BuildOutputPath
                if (DateTime.TryParseExact(st, "yyyy-MM-dd_HHmm", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                    start = EpochTime.DisplayWallClockToUtc(dt);
            }
        }

        return new ParsedPath(show, year, ep, title, start, height, unsorted);
    }

    // =====================================================================
    // League/event linking + refile
    // =====================================================================

    /// <summary>Best-effort link from parsed path metadata to local League/Event rows: league by exact display
    /// name, event by same-day-ish start + loosely matching title. Used by scan adoption and refile.</summary>
    public async Task<(int? LeagueId, int? EventId, string? Sport, long? EventStartUtc)> MatchLeagueEventAsync(ParsedPath parsed, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parsed.ShowName)) return (null, null, null, null);
        var league = await _db.Leagues.AsNoTracking().FirstOrDefaultAsync(l => l.Name == parsed.ShowName, ct);
        if (league is null) return (null, null, null, null);
        if (parsed.StartUtc is not { } ps) return (league.Id, null, league.Sport, null);
        var cands = await _db.Events.AsNoTracking()
            .Where(e => e.LeagueId == league.Id && e.StartUtc >= ps - 86400 && e.StartUtc < ps + 2 * 86400)
            .Select(e => new { e.Id, e.Title, e.StartUtc }).ToListAsync(ct);
        var hit = cands.Where(e => TitlesSimilar(e.Title, parsed.Title))
            .OrderBy(e => Math.Abs(e.StartUtc - ps)).FirstOrDefault();
        return (league.Id, hit?.Id, league.Sport, hit?.StartUtc);
    }

    /// <summary>After a manual import physically moved an item's file, re-point the SAME row at the new location
    /// and refresh its filed metadata (show/season/episode/title from the new path, plus league/event links).
    /// Keeps identity/provenance instead of minting a duplicate and letting the old row rot into Missing.</summary>
    public async Task RefileItemAsync(int itemId, string newPath, CancellationToken ct = default)
    {
        var parsed = ParsePath(newPath);
        var (leagueId, eventId, sport, evStart) = await MatchLeagueEventAsync(parsed, ct);
        await _gate.WriteAsync(async () =>
        {
            var row = await _db.LibraryItems.FindAsync(new object?[] { itemId }, ct);
            if (row is null) return;
            row.FilePath = newPath;
            row.FileBytes = SafeLength(newPath);
            row.Unsorted = parsed.Unsorted;
            if (!string.IsNullOrWhiteSpace(parsed.ShowName)) row.ShowName = parsed.ShowName!;
            if (parsed.SeasonYear != 0) row.SeasonYear = parsed.SeasonYear;
            if (parsed.EpisodeNum != 0) row.EpisodeNum = parsed.EpisodeNum;
            if (!string.IsNullOrWhiteSpace(parsed.Title)) row.Title = parsed.Title;
            row.LeagueId = leagueId ?? row.LeagueId;
            row.EventId = eventId ?? row.EventId;
            row.Sport = sport ?? row.Sport;
            if (evStart is { } s) row.StartUtc = s; else if (parsed.StartUtc is { } p) row.StartUtc = p;
            row.Status = LibraryItemStatus.Ok;
            row.MissingSinceUtc = null;
            row.UpdatedUtc = EpochTime.Now();
            await _db.SaveChangesAsync(ct);
        }, ct);
    }

    private static bool TitlesSimilar(string? a, string? b)
    {
        static string N(string? s) => new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var na = N(a); var nb = N(b);
        return na.Length > 0 && nb.Length > 0 && (na.Contains(nb) || nb.Contains(na));
    }

    // =====================================================================
    // Delete (Library page) — file + sidecars + upward folder prune, contained to the media root
    // =====================================================================

    /// <summary>Delete the item's video file, its metadata sidecars, and any folders the delete emptied (game →
    /// season → show), never touching the media root itself or any folder that still has content. Returns a
    /// user-facing error string, or null on success. The DB row is the caller's to remove.</summary>
    public string? DeleteItemFiles(LibraryItem item)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var errors = new List<string>();
        try
        {
            var root = Norm(_paths.MediaDir);
            var path = Path.GetFullPath(item.FilePath);
            // Containment: only ever delete inside the media root (a corrupted/hand-edited row can't escape).
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "the file path is outside the media folder — refusing to delete";

            if (File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                var baseName = Path.GetFileNameWithoutExtension(path);
                File.Delete(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    // Sidecars named after the video (episode .nfo + thumbnail). Metadata extensions only, so a
                    // neighbouring video can never be swept up.
                    foreach (var side in Directory.EnumerateFiles(dir)
                                 .Where(f => Path.GetFileName(f).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
                                             && Path.GetExtension(f).ToLowerInvariant() is ".nfo" or ".jpg" or ".jpeg" or ".png"))
                        File.Delete(side);

                    // Prune upward: game folder → season folder → show folder, stopping at the first non-empty
                    // level or the media root. A show folder emptied of every game takes its poster/tvshow.nfo
                    // with it (they're the only survivors and belong to nothing once the last game is gone).
                    var cur = dir;
                    while (cur is not null && !string.Equals(Norm(cur), root, StringComparison.OrdinalIgnoreCase))
                    {
                        var entries = Directory.EnumerateFileSystemEntries(cur).ToList();
                        var onlyMetadata = entries.Count > 0 && entries.All(e =>
                            File.Exists(e) && Path.GetExtension(e).ToLowerInvariant() is ".nfo" or ".jpg" or ".jpeg" or ".png");
                        if (entries.Count == 0) Directory.Delete(cur);
                        else if (onlyMetadata) Directory.Delete(cur, recursive: true);
                        else break;
                        cur = Path.GetDirectoryName(cur);
                    }
                }
                _log.LogInformation("[Library] deleted file + sidecars for {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Library] file delete failed for {Path}", item.FilePath);
            errors.Add($"the file couldn't be removed ({ex.Message})");
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
