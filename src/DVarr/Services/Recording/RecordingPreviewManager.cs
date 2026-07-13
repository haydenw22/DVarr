using System.Collections.Concurrent;
using System.Diagnostics;
using DVarr.Data;
using DVarr.Infrastructure;
using DVarr.Services.Media;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Recording;

public enum RecPreviewStatus { Ok, NotFound, NoFootage, Error }
public sealed record RecPreviewResult(RecPreviewStatus Status, string? PlaylistPath, string? Mode);

/// <summary>
/// "Watch while it records": serves an in-progress recording as a growing HLS stream built ENTIRELY from the
/// capture segments already on the local disk — no provider connection, no tuner slot, so it can never fight
/// the recording for the credential. A SegmentStreamer pump feeds the segments into one ffmpeg via stdin;
/// browser-compatible feeds (H.264) are REMUXED with -c copy (near-zero cost, full from-the-start DVR seek),
/// anything else is transcoded (GPU when present) from the live tail. The event-type playlist keeps growing
/// while the capture runs and gains ENDLIST when it stops, so the player rolls seamlessly from live to VOD.
/// Also plays the surviving segments of a FAILED recording (forensics) — it just won't grow. Singleton.
/// </summary>
public sealed class RecordingPreviewManager : IAsyncDisposable
{
    private sealed class Session
    {
        public Process Ff = null!;
        public CancellationTokenSource PumpCts = null!;
        public Task Pump = Task.CompletedTask;
        public string Dir = "";
        public string Mode = "copy";
        public long LastAccessUtc;
    }

    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly FfmpegLocator _ffmpeg;
    private readonly RuntimePaths _paths;
    private readonly RecorderService _recorder;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RecordingPreviewManager> _log;
    private readonly bool _gpu = OperatingSystem.IsLinux() && File.Exists("/dev/nvidia0");

    public RecordingPreviewManager(FfmpegLocator ffmpeg, RuntimePaths paths, RecorderService recorder,
        IServiceScopeFactory scopes, ILogger<RecordingPreviewManager> log)
    { _ffmpeg = ffmpeg; _paths = paths; _recorder = recorder; _scopes = scopes; _log = log; }

    public async Task<RecPreviewResult> EnsureAsync(int recordingId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(recordingId, out var live) && SessionServeable(live))
        {
            Interlocked.Exchange(ref live.LastAccessUtc, EpochTime.Now());
            return new RecPreviewResult(RecPreviewStatus.Ok, Path.Combine(live.Dir, "index.m3u8"), live.Mode);
        }

        await _startGate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(recordingId, out live) && SessionServeable(live))
            {
                live.LastAccessUtc = EpochTime.Now();
                return new RecPreviewResult(RecPreviewStatus.Ok, Path.Combine(live.Dir, "index.m3u8"), live.Mode);
            }
            if (live != null) await KillAsync(recordingId);

            string? segDir;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                segDir = await db.Recordings.AsNoTracking().Where(r => r.Id == recordingId)
                    .Select(r => r.SegmentDir).FirstOrDefaultAsync(ct);
            }
            if (segDir is null) return new RecPreviewResult(RecPreviewStatus.NotFound, null, null);
            var segments = SegmentStreamer.ListSegments(segDir);
            if (segments.Count == 0)
                return new RecPreviewResult(RecPreviewStatus.NoFootage, null, null);

            // Probe the FIRST segment to pick the pipeline: H.264 remuxes losslessly (browser decodes it),
            // anything else (HEVC/MPEG-2/10-bit) needs the transcode. Audio is always normalised to AAC in the
            // copy path too — MP2/AC-3 audio is common on sports feeds and silently unplayable in MSE.
            var probeTarget = segments.Count > 1 ? segments[^2] : segments[0]; // prefer a CLOSED segment
            MediaInfo info;
            using (var scope = _scopes.CreateScope())
                info = await scope.ServiceProvider.GetRequiredService<LibraryService>().ProbeAsync(probeTarget, ct);
            var copyVideo = string.Equals(info.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase);
            var copyAudio = string.Equals(info.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase);

            var dir = Path.Combine(_paths.SegmentDir, "recpreview", recordingId.ToString());
            Directory.CreateDirectory(dir);
            var playlist = Path.Combine(dir, "index.m3u8");

            var attempts = copyVideo
                ? new[] { "copy" }
                : _gpu ? new[] { "nvenc", "x264" } : new[] { "x264" };

            foreach (var mode in attempts)
            {
                foreach (var f in Directory.EnumerateFiles(dir)) { try { File.Delete(f); } catch { } }

                // copy mode replays from the very start (remux is IO-bound — it catches up to live in seconds
                // and yields full DVR seek); a transcode starts near the live edge so it never has to encode
                // hours of backlog before showing the current picture.
                var fromStart = mode == "copy";
                var ff = StartFfmpeg(dir, mode, copyAudio);
                var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                var stdin = ff.StandardInput.BaseStream;
                var pump = Task.Run(async () =>
                {
                    try { await SegmentStreamer.StreamAsync(segDir!, fromStart, () => _recorder.IsActive(recordingId), stdin, pumpCts.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "[RecPreview] pump ended for {Id}", recordingId); }
                    finally { try { stdin.Close(); } catch { } } // EOF → ffmpeg finishes the playlist (ENDLIST)
                }, CancellationToken.None);

                var ready = false;
                for (var i = 0; i < 80; i++) // copy remux is fast; NVENC spin-up can take a few seconds
                {
                    if (SafeHasExited(ff)) break;
                    if (File.Exists(playlist) && Directory.EnumerateFiles(dir, "seg*.ts").Any()) { ready = true; break; }
                    try { await Task.Delay(250, ct); } catch { break; }
                }
                if (ready)
                {
                    _sessions[recordingId] = new Session { Ff = ff, PumpCts = pumpCts, Pump = pump, Dir = dir, Mode = mode, LastAccessUtc = EpochTime.Now() };
                    _log.LogInformation("[RecPreview] recording {Id} previewing from local segments ({Mode})", recordingId, mode);
                    return new RecPreviewResult(RecPreviewStatus.Ok, playlist, mode);
                }

                try { pumpCts.Cancel(); } catch { }
                try { if (!SafeHasExited(ff)) ff.Kill(true); } catch { }
                try { ff.Dispose(); } catch { }
                pumpCts.Dispose();
                if (mode == "nvenc") _log.LogWarning("[RecPreview] NVENC start failed for recording {Id}; falling back to CPU", recordingId);
            }
            return new RecPreviewResult(RecPreviewStatus.Error, null, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[RecPreview] start failed for recording {Id}", recordingId);
            return new RecPreviewResult(RecPreviewStatus.Error, null, null);
        }
        finally { _startGate.Release(); }
    }

    /// <summary>A session can serve while ffmpeg runs, and AFTER it exits too — a finished/settled preview has a
    /// complete VOD playlist on disk that stays playable until the idle sweep reclaims it.</summary>
    private static bool SessionServeable(Session s) => File.Exists(Path.Combine(s.Dir, "index.m3u8"));

    public string? GetFilePath(int recordingId, string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) return null;
        if (!_sessions.TryGetValue(recordingId, out var s)) return null;
        Interlocked.Exchange(ref s.LastAccessUtc, EpochTime.Now());
        var root = Path.GetFullPath(s.Dir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(s.Dir, file));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    private Process StartFfmpeg(string dir, string mode, bool copyAudio)
    {
        var psi = new ProcessStartInfo(_ffmpeg.Ffmpeg)
        { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (mode == "nvenc") args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        // +genpts and +igndts-free defaults: the capture's -copyts absolute timestamps pass straight through on
        // copy; a reconnect's backward PTS jump makes the muxer clamp (warning, not failure).
        args.AddRange(new[] { "-fflags", "+genpts", "-f", "mpegts", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a:0?" });
        switch (mode)
        {
            case "copy":
                args.AddRange(new[] { "-c:v", "copy" });
                break;
            case "nvenc":
                args.AddRange(new[]
                {
                    "-vf", "scale_cuda=-2:720:format=yuv420p",
                    "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-profile:v", "main", "-g", "50", "-no-scenecut", "1",
                });
                break;
            default: // x264
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
            "-f", "hls", "-hls_time", "4",
            // event playlist: segments accumulate (full seek over everything captured so far) and ENDLIST lands
            // when the pump closes stdin — the session dir lives on scratch and is reclaimed by the idle sweep.
            "-hls_playlist_type", "event",
            "-hls_flags", "independent_segments",
            "-hls_segment_type", "mpegts", "-hls_segment_filename", Path.Combine(dir, "seg%d.ts"),
            Path.Combine(dir, "index.m3u8"),
        });
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync() is not null) { } } catch { } });
        _ = Task.Run(async () => { try { while (await p.StandardOutput.ReadLineAsync() is not null) { } } catch { } });
        return p;
    }

    /// <summary>Reclaim sessions nobody has touched recently. A paused player stops fetching segments, so the
    /// window is generous — reopening after a sweep just restarts the session from the segments.</summary>
    public async Task SweepAsync(int idleSeconds = 600)
    {
        var now = EpochTime.Now();
        foreach (var kv in _sessions)
            if (now - Interlocked.Read(ref kv.Value.LastAccessUtc) > idleSeconds)
                await KillAsync(kv.Key);
    }

    public void PurgeOrphanDirs()
    {
        var root = Path.Combine(_paths.SegmentDir, "recpreview");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) { _log.LogDebug(ex, "[RecPreview] boot purge failed"); }
    }

    private async Task KillAsync(int recordingId)
    {
        if (!_sessions.TryRemove(recordingId, out var s)) return;
        try { s.PumpCts.Cancel(); } catch { }
        try { if (!SafeHasExited(s.Ff)) s.Ff.Kill(true); } catch { }
        try { await s.Pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { s.Ff.Dispose(); } catch { }
        try { s.PumpCts.Dispose(); } catch { }
        try { if (Directory.Exists(s.Dir)) Directory.Delete(s.Dir, true); } catch { }
        _log.LogInformation("[RecPreview] session for recording {Id} reclaimed", recordingId);
    }

    private static bool SafeHasExited(Process p) { try { return p.HasExited; } catch { return true; } }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await KillAsync(id);
    }
}
