using System.Collections.Concurrent;
using System.Diagnostics;
using DVarr.Data;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Media;

public enum PlaybackStatus { Ok, NotFound, Missing, Error }
public sealed record PlaybackResult(PlaybackStatus Status, string? PlaylistPath, string? Mode);

/// <summary>
/// In-browser playback for finished library files. One ffmpeg per watched item turns the MKV into an HLS
/// stream in scratch: H.264+AAC files are REMUXED with -c copy (IO-bound — the full seekable timeline appears
/// in seconds), anything the browser can't decode (HEVC, 10-bit, MP2/AC-3 audio) is transcoded, on the GPU
/// when present. The event playlist grows while ffmpeg works and ends with ENDLIST, so hls.js seeks whatever
/// has been prepared and rolls to full VOD when it finishes. Sessions are reclaimed by an idle sweep and on
/// shutdown; reopening a swept item just rebuilds it. Local files only — no tuner slot involved. Singleton.
/// </summary>
public sealed class LibraryPlaybackManager : IAsyncDisposable
{
    private sealed class Session
    {
        public Process Ff = null!;
        public string Dir = "";
        public string Mode = "copy";
        public long LastAccessUtc;
        public System.Collections.Concurrent.ConcurrentQueue<string> StderrTail = new();
    }

    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly FfmpegLocator _ffmpeg;
    private readonly RuntimePaths _paths;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LibraryPlaybackManager> _log;
    private readonly bool _gpu = OperatingSystem.IsLinux() && File.Exists("/dev/nvidia0");

    public LibraryPlaybackManager(FfmpegLocator ffmpeg, RuntimePaths paths, IServiceScopeFactory scopes, ILogger<LibraryPlaybackManager> log)
    { _ffmpeg = ffmpeg; _paths = paths; _scopes = scopes; _log = log; }

    public async Task<PlaybackResult> EnsureAsync(int itemId, bool forceTranscode, CancellationToken ct)
    {
        if (_sessions.TryGetValue(itemId, out var live) && File.Exists(Path.Combine(live.Dir, "index.m3u8"))
            && !(forceTranscode && live.Mode == "copy")) // the browser rejected the copy — rebuild transcoded
        {
            Interlocked.Exchange(ref live.LastAccessUtc, EpochTime.Now());
            return new PlaybackResult(PlaybackStatus.Ok, Path.Combine(live.Dir, "index.m3u8"), live.Mode);
        }

        await _startGate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(itemId, out live) && File.Exists(Path.Combine(live.Dir, "index.m3u8"))
                && !(forceTranscode && live.Mode == "copy"))
            {
                live.LastAccessUtc = EpochTime.Now();
                return new PlaybackResult(PlaybackStatus.Ok, Path.Combine(live.Dir, "index.m3u8"), live.Mode);
            }
            if (live != null) await KillAsync(itemId);

            string? path; string? aCodec; bool copyVideo;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                var item = await db.LibraryItems.AsNoTracking().Where(i => i.Id == itemId)
                    .Select(i => new { i.FilePath, i.VideoCodec, i.AudioCodec }).FirstOrDefaultAsync(ct);
                if (item is null) return new PlaybackResult(PlaybackStatus.NotFound, null, null);
                path = item.FilePath; aCodec = item.AudioCodec;
                if (!File.Exists(path)) return new PlaybackResult(PlaybackStatus.Missing, null, null);
                // The copy decision needs more than the stored codec name: interlaced or 4:2:2/10-bit H.264
                // (real on broadcast-grade 1080 feeds) remuxes fine but decodes badly-or-not-at-all in the
                // browser, so probe the actual bitstream whenever copy is on the table.
                if (forceTranscode)
                    copyVideo = false;
                else
                {
                    var probe = await scope.ServiceProvider.GetRequiredService<LibraryService>().ProbeAsync(path, ct);
                    copyVideo = probe.CopySafeVideo;
                    aCodec ??= probe.AudioCodec;
                }
            }

            var copyAudio = string.Equals(aCodec, "aac", StringComparison.OrdinalIgnoreCase);

            var dir = Path.Combine(_paths.SegmentDir, "playback", itemId.ToString());
            Directory.CreateDirectory(dir);
            var playlist = Path.Combine(dir, "index.m3u8");

            var attempts = copyVideo ? new[] { "copy" } : _gpu ? new[] { "nvenc", "x264" } : new[] { "x264" };
            foreach (var mode in attempts)
            {
                foreach (var f in Directory.EnumerateFiles(dir)) { try { File.Delete(f); } catch { } }
                var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var ff = StartFfmpeg(path!, dir, mode, copyAudio, stderr);
                var ready = false;
                for (var i = 0; i < 80; i++)
                {
                    if (SafeHasExited(ff) && ff.ExitCode != 0) break;
                    if (File.Exists(playlist) && Directory.EnumerateFiles(dir, "seg*.ts").Any()) { ready = true; break; }
                    if (SafeHasExited(ff)) break; // exited 0 without output — nothing to serve
                    try { await Task.Delay(250, ct); } catch { break; }
                }
                if (ready)
                {
                    _sessions[itemId] = new Session { Ff = ff, Dir = dir, Mode = mode, LastAccessUtc = EpochTime.Now(), StderrTail = stderr };
                    _log.LogInformation("[Playback] library item {Id} playing ({Mode})", itemId, mode);
                    return new PlaybackResult(PlaybackStatus.Ok, playlist, mode);
                }
                try { if (!SafeHasExited(ff)) ff.Kill(true); } catch { }
                try { ff.Dispose(); } catch { }
                // The ffmpeg tail is the actual diagnosis — a silent Error status cost a support round-trip once.
                _log.LogWarning("[Playback] {Mode} start failed for item {Id}: {Err}", mode, itemId,
                    stderr.Count > 0 ? string.Join(" | ", stderr) : "(no ffmpeg output)");
            }
            return new PlaybackResult(PlaybackStatus.Error, null, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Playback] start failed for library item {Id}", itemId);
            return new PlaybackResult(PlaybackStatus.Error, null, null);
        }
        finally { _startGate.Release(); }
    }

    public string? GetFilePath(int itemId, string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) return null;
        if (!_sessions.TryGetValue(itemId, out var s)) return null;
        Interlocked.Exchange(ref s.LastAccessUtc, EpochTime.Now());
        var root = Path.GetFullPath(s.Dir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(s.Dir, file));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    private Process StartFfmpeg(string input, string dir, string mode, bool copyAudio, System.Collections.Concurrent.ConcurrentQueue<string> stderrTail)
    {
        var psi = new ProcessStartInfo(_ffmpeg.Ffmpeg)
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (mode == "nvenc") args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        args.AddRange(new[] { "-i", input, "-map", "0:v:0", "-map", "0:a:0?" });
        switch (mode)
        {
            case "copy":
                args.AddRange(new[] { "-c:v", "copy" });
                break;
            case "nvenc":
                args.AddRange(new[]
                {
                    "-vf", "scale_cuda=-2:720:format=yuv420p",
                    "-c:v", "h264_nvenc", "-preset", "p4", "-profile:v", "main", "-g", "50", "-no-scenecut", "1",
                });
                break;
            default:
                args.AddRange(new[]
                {
                    "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
                    "-vf", "scale=-2:720", "-g", "50", "-sc_threshold", "0",
                });
                break;
        }
        if (mode == "copy" && copyAudio) args.AddRange(new[] { "-c:a", "copy" });
        else args.AddRange(new[] { "-c:a", "aac", "-ac", "2", "-b:a", "128k" });
        args.AddRange(new[]
        {
            "-f", "hls", "-hls_time", "6",
            "-hls_playlist_type", "event", // grows while preparing, ENDLIST at completion → full VOD seek
            "-hls_flags", "independent_segments",
            "-hls_segment_type", "mpegts", "-hls_segment_filename", Path.Combine(dir, "seg%d.ts"),
            Path.Combine(dir, "index.m3u8"),
        });
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        // Keep a bounded tail of stderr instead of discarding it — it's the diagnosis when a session fails.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardError.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    stderrTail.Enqueue(line.Trim());
                    while (stderrTail.Count > 20) stderrTail.TryDequeue(out _);
                }
            }
            catch { }
        });
        _ = Task.Run(async () => { try { while (await p.StandardOutput.ReadLineAsync() is not null) { } } catch { } });
        return p;
    }

    /// <summary>Reclaim watched-and-abandoned sessions. Generous window: a PAUSED player fetches nothing, and
    /// reclaiming under it forces a full session rebuild on resume.</summary>
    public async Task SweepAsync(int idleSeconds = 600)
    {
        var now = EpochTime.Now();
        foreach (var kv in _sessions)
            if (now - Interlocked.Read(ref kv.Value.LastAccessUtc) > idleSeconds)
                await KillAsync(kv.Key);
    }

    public void PurgeOrphanDirs()
    {
        var root = Path.Combine(_paths.SegmentDir, "playback");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) { _log.LogDebug(ex, "[Playback] boot purge failed"); }
    }

    /// <summary>Kill a specific item's session (called before its file is deleted from the Library page).</summary>
    public Task StopAsync(int itemId) => KillAsync(itemId);

    private async Task KillAsync(int itemId)
    {
        if (!_sessions.TryRemove(itemId, out var s)) return;
        try
        {
            // A session whose ffmpeg died mid-stream is the "played 6 minutes then stalled" class of bug —
            // surface its stderr tail rather than reclaiming the evidence silently.
            if (SafeHasExited(s.Ff) && s.Ff.ExitCode != 0 && !s.StderrTail.IsEmpty)
                _log.LogWarning("[Playback] item {Id} ffmpeg exited {Code}: {Err}", itemId, s.Ff.ExitCode, string.Join(" | ", s.StderrTail));
        }
        catch { }
        try { if (!SafeHasExited(s.Ff)) s.Ff.Kill(true); } catch { }
        try { s.Ff.Dispose(); } catch { }
        try { if (Directory.Exists(s.Dir)) Directory.Delete(s.Dir, true); } catch { }
        _log.LogInformation("[Playback] session for library item {Id} reclaimed", itemId);
        await Task.CompletedTask;
    }

    private static bool SafeHasExited(Process p) { try { return p.HasExited; } catch { return true; } }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await KillAsync(id);
    }
}
