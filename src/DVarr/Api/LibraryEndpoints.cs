using DVarr.Data;
using DVarr.Infrastructure;
using DVarr.Services.Media;
using DVarr.Services.Recording;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

public record LibraryImportRequest(string? LeagueId, string? EventId);

/// <summary>
/// The Library: what is PHYSICALLY on the media drive, organised the way Plex/Jellyfin see it
/// (league → season → game), with in-browser playback, manual import for unsorted files, delete-from-disk,
/// and an on-demand reconciling scan. Plus the local-segment HLS preview of in-progress recordings.
/// </summary>
public static class LibraryEndpoints
{
    public static void MapLibraryApi(this WebApplication app)
    {
        // ---- Library list (flat rows + rollups; the client groups league → [team →] season → game) ----
        app.MapGet("/api/library", async (DVarrDbContext db, RuntimePaths paths, LibraryScanService scanner) =>
        {
            var items = await db.LibraryItems.AsNoTracking()
                .OrderBy(i => i.ShowName).ThenByDescending(i => i.SeasonYear).ThenByDescending(i => i.StartUtc)
                .ToListAsync();
            var disk = DiskSpace(paths.MediaDir);

            // Team enrichment: each event-linked item carries its home/away TheSportsDB team ids, and each league
            // present in the library reports its FOLLOWED teams — the client uses the pair to group a followed
            // league's games under per-team sections. Two small IN lookups, stitched in memory.
            var evIds = items.Where(i => i.EventId != null).Select(i => i.EventId!.Value).Distinct().ToList();
            var evTeams = evIds.Count == 0
                ? new Dictionary<int, (string? Home, string? Away)>()
                : await db.Events.AsNoTracking().Where(e => evIds.Contains(e.Id))
                    .Select(e => new { e.Id, e.HomeTeamId, e.AwayTeamId })
                    .ToDictionaryAsync(e => e.Id, e => (Home: e.HomeTeamId, Away: e.AwayTeamId));
            var lgIds = items.Where(i => i.LeagueId != null).Select(i => i.LeagueId!.Value).Distinct().ToList();
            var lgFollows = lgIds.Count == 0
                ? new List<object>()
                : (await db.Leagues.AsNoTracking().Where(l => lgIds.Contains(l.Id))
                        .Select(l => new { l.Id, l.MonitoredTeamsJson }).ToListAsync())
                    .Select(l => new { l.Id, Teams = ParseFollowedTeams(l.MonitoredTeamsJson) })
                    .Where(l => l.Teams.Count > 0)
                    .Select(l => (object)new { id = l.Id, followedTeams = l.Teams })
                    .ToList();

            return Results.Json(new
            {
                items = items.Select(i => new
                {
                    i.Id, recordingId = i.RecordingId, eventId = i.EventId, leagueId = i.LeagueId,
                    show = i.ShowName, sport = i.Sport, season = i.SeasonYear, episode = i.EpisodeNum,
                    i.Title, i.StartUtc,
                    path = i.FilePath, bytes = i.FileBytes, durationS = i.DurationS,
                    videoCodec = i.VideoCodec, audioCodec = i.AudioCodec, height = i.Height,
                    channel = i.ChannelName, source = i.SourceLabel,
                    origin = i.Origin.ToString(), status = i.Status.ToString(), unsorted = i.Unsorted,
                    addedUtc = i.CreatedUtc, missingSinceUtc = i.MissingSinceUtc,
                    homeTeamId = i.EventId is { } eid1 && evTeams.TryGetValue(eid1, out var t1) ? t1.Home : null,
                    awayTeamId = i.EventId is { } eid2 && evTeams.TryGetValue(eid2, out var t2) ? t2.Away : null,
                }),
                leagues = lgFollows,
                totals = new
                {
                    count = items.Count,
                    bytes = items.Where(i => i.Status == LibraryItemStatus.Ok).Sum(i => i.FileBytes),
                    missing = items.Count(i => i.Status == LibraryItemStatus.Missing),
                    unsorted = items.Count(i => i.Unsorted && i.Status == LibraryItemStatus.Ok),
                },
                disk,
                lastScanUtc = scanner.LastScanUtc == 0 ? (long?)null : scanner.LastScanUtc,
            });
        });

        // ---- On-demand reconciling scan ----
        app.MapPost("/api/library/scan", async (LibraryScanService scanner, HttpContext ctx) =>
        {
            var s = await scanner.ScanAsync(ctx.RequestAborted);
            return Results.Json(new { ok = s.Skipped is null, seen = s.Seen, adopted = s.Adopted, healed = s.Healed, missing = s.MarkedMissing, tookMs = s.TookMs, skipped = s.Skipped });
        });

        // ---- Delete: the file IS the thing being deleted (a Missing row just removes the entry).
        // The Recording row (if any) is deliberately left alone — it's the scheduler's dedupe history. ----
        app.MapDelete("/api/library/{id:int}", async (int id, DVarrDbContext db, DbWriteGate gate,
            LibraryService lib, LibraryPlaybackManager playback) =>
        {
            var item = await db.LibraryItems.FindAsync(id);
            if (item is null) return Results.NotFound();
            await playback.StopAsync(id); // never delete a file out from under its own playback session
            string? cleanupError = null;
            if (item.Status == LibraryItemStatus.Ok) cleanupError = lib.DeleteItemFiles(item);
            await gate.WriteAsync(async () => { db.LibraryItems.Remove(item); await db.SaveChangesAsync(); });
            return Results.Json(new { deleted = true, fileCleanupError = cleanupError });
        });

        // ---- Manual import (the Unsorted group): file into League/Season/Game and re-point the row ----
        app.MapPost("/api/library/{id:int}/import", async (int id, LibraryImportRequest req, DVarrDbContext db,
            MediaImportService media, LibraryService lib, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LeagueId) || string.IsNullOrWhiteSpace(req.EventId))
                return Results.BadRequest(new { error = "leagueId and eventId are required" });
            var item = await db.LibraryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (item.Status == LibraryItemStatus.Missing) return Results.Json(new { error = "the file is missing on disk" }, statusCode: 409);

            // A recording-backed item goes through the recording path (links the event onto the recording too);
            // an adopted orphan uses the file-based engine. Both end in the identical Plex filing layout.
            var viaRecording = item.RecordingId is { } rid && await db.Recordings.AnyAsync(r => r.Id == rid, ct);
            var (ok, path, error) = viaRecording
                ? await media.AssignAsync(item.RecordingId!.Value, req.LeagueId!.Trim(), req.EventId!.Trim(), ct)
                : await media.AssignFileAsync(item.FilePath, item.StartUtc, req.LeagueId!.Trim(), req.EventId!.Trim(), ct);
            if (!ok) return Results.Json(new { error }, statusCode: 400);
            // The recording path already re-pointed the row via its upsert; the file path needs it done here.
            if (!viaRecording && path is not null) await lib.RefileItemAsync(id, path, ct);
            return Results.Json(new { ok = true, path });
        });

        // ---- Episode thumbnail (the sidecar written at import), falling back to the show poster ----
        app.MapGet("/api/library/{id:int}/thumb", async (int id, DVarrDbContext db, RuntimePaths paths) =>
        {
            var item = await db.LibraryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (item is null) return Results.NotFound();
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.MediaDir)) + Path.DirectorySeparatorChar;
            var dir = Path.GetDirectoryName(item.FilePath);
            var baseName = Path.GetFileNameWithoutExtension(item.FilePath);
            var candidates = new List<string?>();
            if (dir is not null)
            {
                candidates.Add(Path.Combine(dir, baseName + ".jpg"));
                candidates.Add(Path.Combine(dir, baseName + "-thumb.jpg"));
                var season = Path.GetDirectoryName(dir);
                var show = season is null ? null : Path.GetDirectoryName(season);
                if (show is not null) candidates.Add(Path.Combine(show, "poster.jpg"));
            }
            foreach (var c in candidates)
            {
                if (c is null) continue;
                var full = Path.GetFullPath(c);
                // Containment — artwork is only ever served from inside the media root.
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) return Results.File(full, "image/jpeg");
            }
            return Results.NotFound();
        });

        // ---- Watch a library file in the browser (HLS; remux when possible, transcode when needed).
        // ?mode=transcode forces the transcoder — the player sends it automatically after a fatal media error
        // on the direct copy (a bitstream the browser can't decode: splice seam, odd profile, etc.). ----
        app.MapGet("/api/library/{id:int}/play/hls/index.m3u8", async (int id, string? mode, HttpContext ctx, LibraryPlaybackManager mgr) =>
        {
            var r = await mgr.EnsureAsync(id, string.Equals(mode, "transcode", StringComparison.OrdinalIgnoreCase), ctx.RequestAborted);
            return r.Status switch
            {
                PlaybackStatus.Ok => Results.Text(await File.ReadAllTextAsync(r.PlaylistPath!, ctx.RequestAborted), "application/vnd.apple.mpegurl"),
                PlaybackStatus.NotFound => Results.NotFound(),
                PlaybackStatus.Missing => Results.Json(new { error = "file_missing", message = "This file is no longer on disk." }, statusCode: 410),
                _ => Results.Json(new { error = "playback_failed", message = "Could not prepare this file for playback (is ffmpeg available?)." }, statusCode: 502),
            };
        });
        app.MapGet("/api/library/{id:int}/play/hls/{seg}", (int id, string seg, LibraryPlaybackManager mgr) =>
        {
            var path = mgr.GetFilePath(id, seg);
            if (path is null) return Results.NotFound();
            var ctype = seg.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "application/vnd.apple.mpegurl" : "video/mp2t";
            return Results.File(path, ctype);
        });

        // ---- Preview an IN-PROGRESS recording from its local capture segments (no provider slot).
        // Same ?mode=transcode contract as library playback. ----
        app.MapGet("/api/recordings/{id:int}/preview/hls/index.m3u8", async (int id, string? mode, HttpContext ctx, RecordingPreviewManager mgr) =>
        {
            var r = await mgr.EnsureAsync(id, string.Equals(mode, "transcode", StringComparison.OrdinalIgnoreCase), ctx.RequestAborted);
            return r.Status switch
            {
                RecPreviewStatus.Ok => Results.Text(await File.ReadAllTextAsync(r.PlaylistPath!, ctx.RequestAborted), "application/vnd.apple.mpegurl"),
                RecPreviewStatus.NotFound => Results.NotFound(),
                RecPreviewStatus.NoFootage => Results.Json(new { error = "no_footage", message = "Nothing captured yet — try again once the recording has been running a few seconds." }, statusCode: 409),
                _ => Results.Json(new { error = "preview_failed", message = "Could not prepare the preview (is ffmpeg available?)." }, statusCode: 502),
            };
        });
        app.MapGet("/api/recordings/{id:int}/preview/hls/{seg}", (int id, string seg, RecordingPreviewManager mgr) =>
        {
            var path = mgr.GetFilePath(id, seg);
            if (path is null) return Results.NotFound();
            var ctype = seg.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "application/vnd.apple.mpegurl" : "video/mp2t";
            return Results.File(path, ctype);
        });
    }

    /// <summary>League.MonitoredTeamsJson ([{id,name}] written by the Leagues page) → [{id,name}] objects for the
    /// library response. Empty list on null/blank/malformed — team grouping just doesn't apply then.</summary>
    private static List<object> ParseFollowedTeams(string? json)
    {
        var teams = new List<object>();
        if (string.IsNullOrWhiteSpace(json)) return teams;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id)) teams.Add(new { id = id!, name = name ?? id! });
            }
        }
        catch { /* malformed → no team grouping */ }
        return teams;
    }

    /// <summary>Free/total bytes of the filesystem holding the media root — longest-mount-point match so a
    /// container volume (/media) reports ITS backing store, not the root fs. Null when it can't be read.</summary>
    private static object? DiskSpace(string mediaDir)
    {
        try
        {
            // Trailing-separator on BOTH sides ("C:\media\" vs "C:\", "/media/" vs "/media/"): TrimEnding…
            // deliberately never trims a root path, so building the prefix from a trimmed root produced "C:\\"
            // and matched nothing. Longest mount-point match, so /media in a container reports ITS volume.
            static string Sep(string p) => p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
            var full = Sep(Path.GetFullPath(mediaDir));
            var best = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(Sep(d.RootDirectory.FullName), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (best is null) return null;
            return new { freeBytes = best.AvailableFreeSpace, totalBytes = best.TotalSize };
        }
        catch { return null; }
    }
}
