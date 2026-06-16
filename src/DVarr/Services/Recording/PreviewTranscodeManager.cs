using System.Collections.Concurrent;
using System.Diagnostics;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Ingest;
using DVarr.Services.Tuner;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Recording;

public enum PreviewStatus { Ok, NotFound, Disabled, Busy, Error }
public sealed record PreviewResult(PreviewStatus Status, string? PlaylistPath);

/// <summary>
/// On-demand HLS transcode for the live-preview player. Browsers can't decode the HEVC/4K (or AC-3) channels that
/// make up much of the lineup, so when mpegts.js can't play a feed the client falls back here: ONE ffmpeg reads the
/// provider stream (a single connection, so the 1-stream-per-login rule holds) and transcodes to 720p H.264 + AAC
/// HLS, which every browser plays via hls.js. The session holds the credential's slot and is reclaimed by an idle
/// sweeper (or app shutdown), so a closed/forgotten tab frees the slot. Singleton.
/// </summary>
public sealed class PreviewTranscodeManager : IAsyncDisposable
{
    private sealed class Session
    {
        public Process Ff = null!;
        public TunerLease? Lease;
        public string Dir = "";
        public int SourceId;
        public long LastAccessUtc;
    }

    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly FfmpegLocator _ffmpeg;
    private readonly TunerLeaseManager _tuner;
    private readonly RuntimePaths _paths;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PreviewTranscodeManager> _log;

    // Transcode on the GPU (NVDEC decode + scale_cuda + NVENC encode) when an NVIDIA device is present in the
    // container — this is what keeps preview off the CPU. Falls back to libx264 if there's no GPU or a GPU attempt
    // fails at runtime (e.g., the NVENC session limit is hit while Plex is also encoding).
    private readonly bool _gpu = OperatingSystem.IsLinux() && File.Exists("/dev/nvidia0");

    public PreviewTranscodeManager(FfmpegLocator ffmpeg, TunerLeaseManager tuner, RuntimePaths paths,
        IServiceScopeFactory scopes, ILogger<PreviewTranscodeManager> log)
    { _ffmpeg = ffmpeg; _tuner = tuner; _paths = paths; _scopes = scopes; _log = log; }

    public async Task<PreviewResult> EnsureAsync(int channelId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(channelId, out var live) && !SafeHasExited(live.Ff))
        {
            Interlocked.Exchange(ref live.LastAccessUtc, EpochTime.Now());
            return new PreviewResult(PreviewStatus.Ok, Path.Combine(live.Dir, "index.m3u8"));
        }

        await _startGate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the gate (another request may have started it).
            if (_sessions.TryGetValue(channelId, out live) && !SafeHasExited(live.Ff))
            {
                live.LastAccessUtc = EpochTime.Now();
                return new PreviewResult(PreviewStatus.Ok, Path.Combine(live.Dir, "index.m3u8"));
            }
            if (live != null) await KillAsync(channelId); // clean up a dead session

            string url; int sourceId; string? ua; bool needsLease;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                var xtream = scope.ServiceProvider.GetRequiredService<XtreamClient>();
                var ch = await db.Channels.FindAsync(new object?[] { channelId }, ct);
                if (ch is null) return new PreviewResult(PreviewStatus.NotFound, null);
                var src = await db.Sources.FindAsync(new object?[] { ch.SourceId }, ct);
                if (src is null) return new PreviewResult(PreviewStatus.NotFound, null);
                if (!src.Enabled) return new PreviewResult(PreviewStatus.Disabled, null);
                sourceId = src.Id;
                ua = src.UserAgent;
                needsLease = string.IsNullOrWhiteSpace(ch.DirectUrl);
                url = needsLease ? xtream.StreamTsUrl(src, ch.StreamId) : ch.DirectUrl!;
            }

            // Acquire the slot. Retry briefly: a just-closed mpegts preview on the same credential may still be
            // releasing its lease when the client falls back to transcode.
            TunerLease? lease = null;
            if (needsLease)
            {
                for (var i = 0; i < 6 && lease is null; i++)
                {
                    lease = await _tuner.TryAcquireAsync(sourceId, LeasePurpose.Live, null, channelId, null, ct);
                    if (lease is null) { try { await Task.Delay(500, ct); } catch { } }
                }
                if (lease is null) return new PreviewResult(PreviewStatus.Busy, null);
            }

            try
            {
                var dir = Path.Combine(_paths.SegmentDir, "preview", channelId.ToString());
                Directory.CreateDirectory(dir);
                var playlist = Path.Combine(dir, "index.m3u8");

                // Try the GPU (NVENC) first when a GPU is present, then fall back to CPU (libx264). The credential
                // lease is held across attempts so a failed GPU start doesn't drop (and re-fight for) the slot.
                foreach (var useNvenc in (_gpu ? new[] { true, false } : new[] { false }))
                {
                    foreach (var f in Directory.EnumerateFiles(dir)) { try { File.Delete(f); } catch { } }
                    var ff = StartFfmpeg(url, ua, dir, useNvenc);

                    // Wait for the playlist + first segment to materialise (or ffmpeg to die).
                    var ready = false;
                    for (var i = 0; i < 60; i++)
                    {
                        if (SafeHasExited(ff)) break;
                        if (File.Exists(playlist) && Directory.EnumerateFiles(dir, "*.ts").Any()) { ready = true; break; }
                        try { await Task.Delay(250, ct); } catch { break; }
                    }
                    if (ready)
                    {
                        var session = new Session { Ff = ff, Lease = lease, Dir = dir, SourceId = sourceId, LastAccessUtc = EpochTime.Now() };
                        _sessions[channelId] = session;
                        lease = null; // ownership transferred to the session; the catch below must not double-release it
                        _log.LogInformation("[PreviewTranscode] channel {Id} transcoding on {Where}", channelId, useNvenc ? "GPU (NVENC)" : "CPU (libx264)");
                        return new PreviewResult(PreviewStatus.Ok, playlist);
                    }

                    // This attempt failed: kill its ffmpeg and try the next encoder (GPU → CPU).
                    try { if (!SafeHasExited(ff)) ff.Kill(true); } catch { }
                    try { ff.Dispose(); } catch { }
                    if (useNvenc) _log.LogWarning("[PreviewTranscode] NVENC start failed for channel {Id}; falling back to CPU (libx264)", channelId);
                }

                if (lease is not null) await _tuner.ReleaseAsync(lease, CancellationToken.None);
                return new PreviewResult(PreviewStatus.Error, null);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PreviewTranscode] start failed for channel {Id}", channelId);
                if (lease is not null) await _tuner.ReleaseAsync(lease, CancellationToken.None);
                return new PreviewResult(PreviewStatus.Error, null);
            }
        }
        finally { _startGate.Release(); }
    }

    /// <summary>Resolve a session file (playlist or segment), updating last-access. Null if no session / path escapes.</summary>
    public string? GetFilePath(int channelId, string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) return null; // flat seg names only
        if (!_sessions.TryGetValue(channelId, out var s)) return null;
        Interlocked.Exchange(ref s.LastAccessUtc, EpochTime.Now());
        var root = Path.GetFullPath(s.Dir) + Path.DirectorySeparatorChar; // trailing sep: 'preview/12' must not match 'preview/123'
        var full = Path.GetFullPath(Path.Combine(s.Dir, file));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    private Process StartFfmpeg(string url, string? userAgent, string dir, bool useNvenc)
    {
        var psi = new ProcessStartInfo(_ffmpeg.Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (!string.IsNullOrWhiteSpace(userAgent)) { args.Add("-user_agent"); args.Add(userAgent!); }
        // GPU path: decode on NVDEC and keep frames in CUDA memory so scale + encode also run on the GPU.
        if (useNvenc) args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        args.AddRange(new[] { "-fflags", "+genpts", "-i", url, "-map", "0:v:0", "-map", "0:a:0?" });
        // Always transcode (we can't probe codecs without a 2nd provider connection): 720p H.264 + stereo AAC plays
        // in every browser. Video on the GPU when available, else libx264.
        if (useNvenc)
            args.AddRange(new[]
            {
                // scale_cuda's format=yuv420p down-converts 10-bit HEVC (common on sports channels) to 8-bit ON the
                // GPU — h264_nvenc can't encode 10-bit, and this avoids a CPU round-trip. p4/ll = fast low-latency.
                "-vf", "scale_cuda=-2:720:format=yuv420p",
                "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-profile:v", "main", "-g", "50", "-no-scenecut", "1",
            });
        else
            args.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-pix_fmt", "yuv420p",
                "-vf", "scale=-2:720", "-g", "50", "-sc_threshold", "0",
            });
        args.AddRange(new[]
        {
            "-c:a", "aac", "-ac", "2", "-b:a", "128k",
            "-f", "hls", "-hls_time", "2", "-hls_list_size", "6",
            "-hls_flags", "delete_segments+omit_endlist+independent_segments",
            "-hls_segment_type", "mpegts", "-hls_segment_filename", Path.Combine(dir, "seg%d.ts"),
            Path.Combine(dir, "index.m3u8"),
        });
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync() is { } _) { } } catch { } });
        _ = Task.Run(async () => { try { while (await p.StandardOutput.ReadLineAsync() is { } _) { } } catch { } });
        return p;
    }

    /// <summary>Reclaim sessions with no recent access (closed/forgotten player) — frees the credential's slot.</summary>
    public async Task SweepAsync(int idleSeconds = 30)
    {
        var now = EpochTime.Now();
        foreach (var kv in _sessions)
        {
            var idle = now - Interlocked.Read(ref kv.Value.LastAccessUtc) > idleSeconds;
            if (idle || SafeHasExited(kv.Value.Ff)) await KillAsync(kv.Key);
        }
    }

    /// <summary>Boot purge: no sessions exist yet, so any leftover preview dirs are orphans from a previous run.</summary>
    public void PurgeOrphanDirs()
    {
        var root = Path.Combine(_paths.SegmentDir, "preview");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception ex) { _log.LogDebug(ex, "[PreviewTranscode] boot purge failed"); }
    }

    private async Task KillAsync(int channelId)
    {
        if (!_sessions.TryRemove(channelId, out var s)) return;
        try { if (!SafeHasExited(s.Ff)) s.Ff.Kill(true); } catch { }
        try { s.Ff.Dispose(); } catch { }
        if (s.Lease is not null)
            try { await _tuner.ReleaseAsync(s.Lease, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "[PreviewTranscode] lease release failed for channel {Id}", channelId); }
        try { if (Directory.Exists(s.Dir)) Directory.Delete(s.Dir, true); } catch { }
        _log.LogInformation("[PreviewTranscode] session for channel {Id} reclaimed", channelId);
    }

    private static bool SafeHasExited(Process p) { try { return p.HasExited; } catch { return true; } }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await KillAsync(id);
    }
}

/// <summary>Purges orphan preview dirs on boot, periodically reclaims idle sessions, and tears everything down on
/// graceful shutdown so no ffmpeg process / stream slot / temp dir is left behind.</summary>
public sealed class PreviewSweeper : BackgroundService
{
    private readonly PreviewTranscodeManager _mgr;
    public PreviewSweeper(PreviewTranscodeManager mgr) { _mgr = mgr; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mgr.PurgeOrphanDirs(); // no sessions at boot → any leftover preview dirs are stale
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch { break; }
            try { await _mgr.SweepAsync(); } catch { }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await _mgr.DisposeAsync(); } catch { } // kill all ffmpeg + release leases while DI is still alive
        await base.StopAsync(cancellationToken);
    }
}
