using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Media;
using DVarr.Services.Tuner;
using Microsoft.EntityFrameworkCore;
using RecordingEntity = DVarr.Data.Entities.Recording;

namespace DVarr.Services.Recording;

/// <summary>
/// The reliability engine (docs/04). One supervisor drives one recording through the
/// canonical state machine. It captures short MPEG-TS segments and — the core fix for
/// legacy-DVR bug #1 (a source-forced ffmpeg exit ended the capture) — RELAUNCHES ffmpeg on any exit/stall, holding the tuner lease across
/// the relaunch, and NEVER enters a terminal failure state before post-roll. At the window
/// end it concatenates the closed segments losslessly into the final file.
/// </summary>
public sealed class RecorderSupervisor
{
    public sealed record Deps(
        IServiceScopeFactory Scopes,
        DbWriteGate Gate,
        FfmpegLocator Ffmpeg,
        TunerLeaseManager Tuner,
        RecordingEventBus Bus,
        ILoggerFactory LoggerFactory);

    private readonly Deps _d;
    private readonly ILogger _log;

    public RecorderSupervisor(Deps d)
    {
        _d = d;
        _log = d.LoggerFactory.CreateLogger("DVarr.Recorder");
    }

    // Backoff schedule between relaunch attempts (seconds): 0, 2, 5, 10, then steady 15.
    private static readonly int[] Backoff = { 0, 2, 5, 10 };
    private const long MinGrowthBytes = 4096;

    // If a line cleanly EOFs more than this many times in 30s it's flapping pathologically — throttle the
    // otherwise-instant relaunch with a short back-off so we don't spin.
    private const int MaxCleanEofPer30s = 8;

    /// <summary>How one ffmpeg capture pass ended — drives whether RunAsync relaunches instantly (clean EOF) or
    /// walks the back-off + failover ladder (a real fault).</summary>
    private enum ExitKind { WindowClosed, StopRequested, CleanEof, Crashed, Stalled, DeadPicture }
    private sealed record CaptureExit(ExitKind Kind, string Detail);

    /// <summary>
    /// Run the full lifecycle. <paramref name="getNextFallbackUrl"/> returns the next
    /// SAME-CREDENTIAL fallback stream URL (or null when none remain). The lease is released
    /// when this returns.
    /// </summary>
    public async Task RunAsync(
        int recordingId,
        string initialUrl,
        string segDir,
        string outputPath,
        long windowEndUtc,
        int stallTimeoutS,
        bool nativeRate,
        bool contentVerify,
        int contentDeadTimeoutS,
        string? contentVerifyHwaccel,
        int contentVerifyFps,
        bool cleanEofInstantRelaunch,
        string? userAgent,
        TunerLease lease,
        Func<int, Task<(int channelId, int streamId, string url)?>> getNextFallbackUrl,
        CancellationToken stopToken)
    {
        Directory.CreateDirectory(segDir);
        var url = initialUrl;
        var attempt = 0;
        var fallbacksUsed = 0;
        var cleanEofWindow = new Queue<long>(); // wall-clock marks of recent clean EOFs (flap detector)

        // ---- Live window end (Phase 21 smart auto-stop) ----
        // AutoStopService may EXTEND Recording.EndUtc mid-capture (extra time / penalties) or trim an unused
        // extension back once the guide reports the event finished. The windowEndUtc PARAMETER is therefore only
        // the INITIAL value; this throttled reader re-checks the row (at most one DB read per 15s — the capture
        // monitor already wakes every 3s, so an extension is picked up well within a step) and refreshes the
        // effective window. SAFETY: the result is floored at the initial windowEndUtc, so a missing row, a read
        // failure, or ANY external shrink can never end a capture before the window it was armed with — with no
        // auto-stop writes this behaves exactly like the old read-once local.
        var windowEnd = windowEndUtc;
        long windowEndReadUtc = 0;
        async Task<long> CurrentWindowEndAsync()
        {
            var nowW = EpochTime.Now();
            if (nowW - windowEndReadUtc >= 15)
            {
                windowEndReadUtc = nowW;
                windowEnd = Math.Max(windowEndUtc, await ReadWindowEndAsync(recordingId, windowEnd));
            }
            return windowEnd;
        }

        try
        {
            await SetStateAsync(recordingId, RecordingState.Starting, r => { r.SegmentDir = segDir; r.OutputPath = outputPath; });

            while (!stopToken.IsCancellationRequested && EpochTime.Now() < await CurrentWindowEndAsync())
            {
                try
                {
                    var exit = await CaptureUntilStopOrStallAsync(recordingId, url, segDir, CurrentWindowEndAsync, stallTimeoutS, nativeRate, contentVerify, contentDeadTimeoutS, contentVerifyHwaccel, contentVerifyFps, userAgent, lease, stopToken);

                    // Normal end of window or explicit stop → leave the capture loop and finalize.
                    if (stopToken.IsCancellationRequested || EpochTime.Now() >= await CurrentWindowEndAsync())
                        break;
                    if (exit.Kind is ExitKind.WindowClosed or ExitKind.StopRequested)
                        break;

                    // A CLEAN end-of-file (rc=0) mid-window is a momentary line drop, not a fault: relaunch
                    // IMMEDIATELY, stay in Recording, and don't emit Recovering churn. The finalize de-overlap
                    // removes any few seconds the provider re-serves on reconnect. Only throttle (2s) if the line
                    // is flapping pathologically (many clean EOFs in 30s).
                    // A native-rate input (VOD / test / DirectUrl) that cleanly EOFs has simply ENDED — finalize what
                    // we captured, never loop it. Only a LIVE clean EOF is a momentary line drop worth relaunching.
                    if (exit.Kind == ExitKind.CleanEof && nativeRate) break;
                    if (exit.Kind == ExitKind.CleanEof && cleanEofInstantRelaunch && !nativeRate)
                    {
                        var nowEof = EpochTime.Now();
                        cleanEofWindow.Enqueue(nowEof);
                        while (cleanEofWindow.Count > 0 && nowEof - cleanEofWindow.Peek() > 30) cleanEofWindow.Dequeue();
                        if (cleanEofWindow.Count > MaxCleanEofPer30s)
                        {
                            _log.LogWarning("[Recorder] Recording {Id} clean-EOF flapping ({N}/30s); throttling relaunch 2s", recordingId, cleanEofWindow.Count);
                            try { await Task.Delay(TimeSpan.FromSeconds(2), stopToken); } catch (OperationCanceledException) { break; }
                        }
                        continue; // instant relaunch — no attempt++, no state change
                    }

                    // Recoverable FAULT (crash / stall / dead picture): relaunch — never fail.
                    attempt++;
                    await SetStateAsync(recordingId, RecordingState.Recovering,
                        r => r.AttemptCount = attempt,
                        NotificationKind.StalledRelaunched, Severity.Warn,
                        $"ffmpeg {exit.Detail} (attempt {attempt}); relaunching");

                    // After several attempts on one channel, try a same-credential fallback.
                    if (attempt % 3 == 0)
                    {
                        var next = await getNextFallbackUrl(recordingId);
                        if (next is { } fb)
                        {
                            fallbacksUsed++;
                            url = fb.url;
                            await SetStateAsync(recordingId, RecordingState.FailingOver,
                                r => { r.ChannelId = fb.channelId; r.StreamId = fb.streamId; },
                                NotificationKind.FailedOver, Severity.Warn, "switching to same-credential fallback channel");
                        }
                        else
                        {
                            // No fallback left → DEGRADED, but keep retrying the primary until post-roll.
                            await SetStateAsync(recordingId, RecordingState.Degraded, null,
                                NotificationKind.Degraded, Severity.Warn, "no same-credential fallback; retrying primary");
                        }
                    }

                    var delay = attempt <= Backoff.Length ? Backoff[attempt - 1] : 15;
                    try { await Task.Delay(TimeSpan.FromSeconds(delay), stopToken); } catch (OperationCanceledException) { break; }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception exLoop)
                {
                    // CRITICAL invariant: a transient fault (DB hiccup, process API, I/O) must NOT escape
                    // and abandon already-captured segments. Recover and keep going until post-roll.
                    _log.LogError(exLoop, "[Recorder] Recording {Id} transient capture-loop error; recovering", recordingId);
                    attempt++;
                    try
                    {
                        await SetStateAsync(recordingId, RecordingState.Recovering, r => r.AttemptCount = attempt,
                            NotificationKind.StalledRelaunched, Severity.Warn, "transient error: " + exLoop.Message);
                    }
                    catch { /* state write itself failed; still loop on */ }
                    var delay = attempt <= Backoff.Length ? Backoff[attempt - 1] : 15;
                    try { await Task.Delay(TimeSpan.FromSeconds(delay), stopToken); } catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception exOuter)
        {
            _log.LogError(exOuter, "[Recorder] Recording {Id} pre-finalize error", recordingId);
        }

        // Capture has ended and its ffmpeg is already graceful-stopped/disposed (inside the capture loop), so the
        // provider connection is closed. Finalize is purely LOCAL (concat + AAC re-encode of on-disk segments) and
        // never touches the provider — so release the credential's single stream slot NOW rather than pinning it for
        // the whole finalize (up to ~60 min on a long recording), which would block a back-to-back same-credential
        // recording from arming. ReleaseAsync is idempotent (Interlocked guard), so the finally below is a safe no-op.
        await _d.Tuner.ReleaseAsync(lease);

        // ALWAYS finalize — even after an unexpected error, concatenate whatever segments exist so a
        // transient fault never abandons a captured window. NeedsAttention only if there is truly nothing.
        try
        {
            await FinalizeToTerminalAsync(recordingId, segDir, outputPath, fallbacksUsed);
        }
        catch (Exception exFin)
        {
            _log.LogError(exFin, "[Recorder] Recording {Id} finalize error", recordingId);
            try
            {
                await SetStateAsync(recordingId, RecordingState.NeedsAttention,
                    r => r.FailureReason = "finalize error: " + exFin.Message,
                    NotificationKind.NeedsAttention, Severity.Critical, exFin.Message);
            }
            catch { }
        }
        finally
        {
            await _d.Tuner.ReleaseAsync(lease);
        }
    }

    /// <summary>
    /// Stopping → Finalizing → concat → DONE (or NEEDS_ATTENTION only if no playable output exists).
    /// Public so catch-up-on-boot can re-finalize a window whose segments survived a crash (docs/05 §3.4).
    /// </summary>
    public async Task FinalizeToTerminalAsync(int recordingId, string segDir, string outputPath, int fallbacksUsed = 0)
    {
        await SetStateAsync(recordingId, RecordingState.Stopping);
        await SetStateAsync(recordingId, RecordingState.Finalizing);
        var (ok, bytes, durationS, gaps) = await FinalizeAsync(recordingId, segDir, outputPath);
        if (ok)
        {
            // Phase 3: file event-linked recordings into the Plex/Jellyfin library (.nfo + thumbnail).
            var finalPath = outputPath;
            try
            {
                using var scope = _d.Scopes.CreateScope();
                var media = scope.ServiceProvider.GetRequiredService<MediaImportService>();
                finalPath = await media.ImportAsync(recordingId, outputPath);
            }
            catch (Exception ex) { _log.LogWarning(ex, "[Recorder] media import failed for {Id}", recordingId); }

            await SetStateAsync(recordingId, RecordingState.Done, r =>
            {
                r.OutputPath = finalPath;
                r.BytesWritten = bytes;
                r.GapsJson = gaps;
            }, NotificationKind.Completed, Severity.Info,
                $"completed: {bytes / 1_000_000} MB, ~{durationS}s" + (fallbacksUsed > 0 ? $", {fallbacksUsed} failover(s)" : ""));

            // The recording is safely finalized + imported (terminal Done). The 8s TS segments were pure scratch —
            // delete them so they don't accumulate (each recording is 6GB+, which would otherwise double its storage
            // forever). Safe because boot-recovery only re-finalizes NON-terminal rows; a Done recording is never
            // re-finalized. Only runs on success — on failure (below) segments are kept for retry/forensics.
            try
            {
                // Remove the whole per-recording scratch dir (/segments/{id}), not just the capture-chain
                // subdir (/segments/{id}/A), so no empty {id}/ folders accumulate over time.
                var recScratch = Path.GetDirectoryName(segDir) ?? segDir; // segDir = /segments/{id}/A
                if (Directory.Exists(recScratch)) Directory.Delete(recScratch, recursive: true);
                _log.LogInformation("[Recorder] Recording {Id}: cleaned up segment scratch", recordingId);
            }
            catch (Exception ex) { _log.LogDebug(ex, "[Recorder] Recording {Id}: segment cleanup skipped (harmless)", recordingId); }
        }
        else
        {
            await SetStateAsync(recordingId, RecordingState.NeedsAttention,
                r => r.FailureReason = "finalize produced no playable output",
                NotificationKind.NeedsAttention, Severity.Critical, "finalize failed (no playable output)");
        }
    }

    /// <summary>Launch one ffmpeg, watch output growth, return a typed reason when it exits or stalls.
    /// <paramref name="windowEndAsync"/> is RunAsync's throttled live window-end reader (initial value floored),
    /// evaluated by the 3s monitor loop below so a mid-capture auto-stop extension moves the cut-off without
    /// touching ffmpeg — the segmenter just keeps rolling until the (possibly extended) window closes.</summary>
    private async Task<CaptureExit> CaptureUntilStopOrStallAsync(
        int recordingId, string url, string segDir, Func<Task<long>> windowEndAsync, int stallTimeoutS, bool nativeRate,
        bool contentVerify, int contentDeadTimeoutS, string? contentVerifyHwaccel, int contentVerifyFps,
        string? userAgent, TunerLease lease, CancellationToken stopToken)
    {
        var pattern = Path.Combine(segDir, "seg-%Y%m%d-%H%M%S.ts");
        var psi = new ProcessStartInfo(_d.Ffmpeg.Ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1" };
        // Manual/test inputs (VOD) are read at native rate so they behave like a live feed
        // instead of being slurped at full speed; real provider feeds are already live.
        if (nativeRate) args.Add("-re");
        // Use the source's configured user-agent if set (some IPTV providers require a specific UA); else the VLC default.
        args.Add("-user_agent");
        args.Add(string.IsNullOrWhiteSpace(userAgent) ? DVarr.Services.Ingest.XtreamClient.DefaultUserAgent : userAgent!);
        args.AddRange(new[]
        {
            "-reconnect", "1", "-reconnect_streamed", "1",
            "-reconnect_on_network_error", "1", "-reconnect_delay_max", "10", "-rw_timeout", "15000000",
        });
        // -reconnect_at_eof only for LIVE inputs. A native-rate (VOD/DirectUrl) input is finite and cleanly hits
        // EOF; reconnect_at_eof would then loop forever retrying the end of file (it never "comes back"), so the
        // segmenter writes nothing. Live provider streams never intentionally EOF, so they keep it to ride out blips.
        if (!nativeRate) args.AddRange(new[] { "-reconnect_at_eof", "1" });
        // Decode the content-verify pass on the GPU (NVDEC) when enabled — an INPUT option, so it applies to the
        // single shared decode that feeds the black/freeze filters. The -c copy recording output never decodes, so
        // it's untouched. Skipped when content-verify is off (nothing decodes) or hwaccel is none/software.
        var cvHw = (contentVerifyHwaccel ?? "").Trim();
        if (contentVerify && cvHw.Length > 0 && !cvHw.Equals("none", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-hwaccel", cvHw });
        args.AddRange(new[]
        {
            // +genpts ONLY (fill MISSING timestamps). We DROPPED +igndts — it told ffmpeg to trust a corrupt
            // source PTS over the good DTS, which is exactly what baked a single +25h PTS spike into a recording
            // and inflated its duration to bogus hours — and +discardcorrupt, which can silently drop audio TS
            // packets. Larger probe so HE-AAC/SBR audio + the PMT are fully detected before -c copy begins.
            "-fflags", "+genpts", "-analyzeduration", "10M", "-probesize", "10M",
            "-i", url,
            // Explicit 1 video + 1 audio. NOT -map 0 — on a multi-variant HLS master that would pull every
            // bitrate rendition at once and choke startup. Explicit mapping is also deterministic if the
            // provider emits a transient extra stream/PMT mid-feed. Audio is OPTIONAL (0:a?) so a video-only or
            // delayed-audio feed records instead of ffmpeg exiting "Stream map '0:a' matches no streams".
            "-map", "0:v", "-map", "0:a?", "-c", "copy", "-max_muxing_queue_size", "4096",
            // Self-heal a corrupt source PTS AT CAPTURE: rewrite PTS from the (good) DTS only when they diverge
            // by >10s (900000 ticks). Real B-frame reorder is <0.15s, so this is a pure no-op on healthy frames
            // (a ~700x safety margin) and preserves legitimate PTS ordering — but a single rogue source PTS can
            // no longer poison the whole recording's timeline. Stays -c copy (no decode). Commas in the
            // expression are ffmpeg-escaped (\\, -> \, at the arg level).
            "-bsf:v", "setts=pts=if(gt(abs(PTS-DTS)\\,900000)\\,DTS\\,PTS)",
            // -copyts + NO -reset_timestamps: keep every segment on the source's CONTINUOUS absolute PTS. Previously
            // -reset_timestamps re-zeroed each segment, so when a flaky line reconnected and the provider re-served a
            // few buffered seconds, the duplicate played as a fresh 0-based segment and finalize concatenated it
            // verbatim → the file "went back in time". With a continuous timeline the re-served packets carry a
            // BACKWARD pts, which FinalizeAsync detects and trims (the de-overlap pass). This is the core fix.
            "-copyts",
            // Cut on WALL-CLOCK time, not stream PTS: live IPTV feeds reset/jump their timestamps (ad breaks, encoder
            // restarts, feed switches), and a PTS-based segmenter then stops cutting and grows one giant segment.
            // segment_atclocktime makes rotation immune to those discontinuities (still cuts at the next keyframe,
            // so every segment still starts on a keyframe — important for clean de-overlap seeks at finalize).
            "-f", "segment", "-segment_time", "8", "-segment_atclocktime", "1", "-segment_format", "mpegts",
            "-strftime", "1",
            pattern,
        });
        // Content verification (opt-in): a SECOND output on the SAME ffmpeg — one provider connection, so the
        // 1-stream-per-login rule is preserved — decodes the already-open feed and emits black/freeze/silence
        // metadata on stderr. The recording output above is untouched (lossless -c copy); this costs CPU only.
        // The filter d=2 just makes events fire promptly; the real "is it dead long enough to fail over" decision
        // is the contentDeadTimeoutS wall-clock check in the monitor loop. Validated not to false-positive on
        // continuous live content (only a genuine static/black/frozen slate trips it).
        if (contentVerify)
        {
            // fps=N (when >0) caps the black/freeze sample rate, so the GPU only hands ~N decoded frames/sec to the
            // CPU filters — a dead slate is just as detectable at 1fps, and this is what makes the pass near-free.
            // Audio silencedetect stays full-rate (audio decode is trivial). The d=2 thresholds still fire after ~2s.
            var vf = (contentVerifyFps > 0 ? $"fps={contentVerifyFps}," : "")
                   + "blackdetect=d=2:pic_th=0.98,freezedetect=n=0.001:d=2";
            args.AddRange(new[]
            {
                "-map", "0:v:0", "-map", "0:a:0?",
                "-vf", vf,
                "-af", "silencedetect=n=-50dB:d=2",
                "-f", "null", "-",
            });
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stderrTail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var prog = new Progress();
        var content = new ContentMonitor();
        proc.Start();

        // Parse ffmpeg -progress on stdout. out_time_us / total_size are reliable progress
        // signals even on Windows, where an actively-written segment file's size on disk lags.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq);
                    var val = line.Substring(eq + 1);
                    if (key == "out_time_us" && long.TryParse(val, out var ot)) prog.SetOutTime(ot);
                    else if (key == "total_size" && long.TryParse(val, out var ts)) prog.SetTotalSize(ts);
                }
            }
            catch { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                {
                    if (contentVerify) content.Observe(line, EpochTime.Now());
                    stderrTail.Enqueue(line);
                    while (stderrTail.Count > 20) stderrTail.TryDequeue(out _);
                }
            }
            catch { }
        });

        long lastOutUs = -1, lastBytes = -1;
        var lastFileCount = -1;
        var lastGrowth = EpochTime.Now();
        var promoted = false;
        var placeholderNotified = false;

        while (true)
        {
            if (proc.HasExited)
            {
                var rc = proc.ExitCode;
                var tail = string.Join(" | ", stderrTail.TakeLast(3));
                // rc==0 is a clean EOF (the live input ended cleanly — a momentary line drop). rc!=0 is a real crash.
                return new CaptureExit(rc == 0 ? ExitKind.CleanEof : ExitKind.Crashed, $"exited rc={rc}: {tail}");
            }

            if (stopToken.IsCancellationRequested || EpochTime.Now() >= await windowEndAsync())
            {
                await GracefulStopAsync(proc);
                return new CaptureExit(ExitKind.WindowClosed, "window closed / stop requested");
            }

            var outUs = prog.OutTimeUs;
            var (diskBytes, fileCount) = MeasureSegments(segDir);
            var bytes = Math.Max(prog.TotalSize, diskBytes);

            // Progress = ANY independent signal advancing by its own sensible minimum (units kept separate,
            // not folded into one scalar): ffmpeg output timestamp grew, bytes grew >= MinGrowthBytes, or a
            // new segment closed. This avoids the byte-threshold being applied to a millisecond/proxy value.
            var progressed = outUs > lastOutUs || bytes >= lastBytes + MinGrowthBytes || fileCount > lastFileCount;
            if (progressed)
            {
                lastOutUs = Math.Max(lastOutUs, outUs);
                lastBytes = Math.Max(lastBytes, bytes);
                lastFileCount = Math.Max(lastFileCount, fileCount);
                lastGrowth = EpochTime.Now();
                await _d.Tuner.HeartbeatAsync(lease.Id, bytes);
                // When verification is on, a tick with live picture (not black/frozen) is "content OK" — stamp it so
                // the UI/notifications can show the last time real picture was seen.
                long? contentOk = contentVerify && content.DeadPictureSeconds(EpochTime.Now()) == 0 ? EpochTime.Now() : null;
                if (!promoted)
                {
                    promoted = true;
                    await SetStateAsync(recordingId, RecordingState.Recording, r => { r.BytesWritten = bytes; if (contentOk is { } co) r.LastContentOkUtc = co; }, NotificationKind.Started, Severity.Info, "capture live");
                }
                else
                {
                    await UpdateProgressAsync(recordingId, bytes, contentOk);
                }
            }
            else
            {
                // Longer grace for the FIRST frame (HLS/stream cold-open), then the configured stall timeout.
                var grace = promoted ? stallTimeoutS : Math.Max(stallTimeoutS, 45);
                if (EpochTime.Now() - lastGrowth > grace)
                {
                    _log.LogWarning("[Recorder] Recording {Id} STALLED ({Sec}s no progress); killing ffmpeg to relaunch", recordingId, grace);
                    await GracefulStopAsync(proc, hardKill: true);
                    return new CaptureExit(ExitKind.Stalled, $"stalled ({grace}s no progress)");
                }
            }

            // Dead-feed check (only when verification is enabled): the transport is alive (bytes flowing) but the
            // PICTURE is a sustained black/frozen slate — a provider "channel offline" card, a stuck decoder, etc.
            // Audio silence alone never trips this: a quiet moment in play is not a dead feed. Sustained dead picture
            // routes into the SAME relaunch→failover ladder as a stall, so it walks to the next-ranked channel.
            if (contentVerify)
            {
                var deadSecs = content.DeadPictureSeconds(EpochTime.Now());
                if (deadSecs >= contentDeadTimeoutS)
                {
                    var what = content.Black ? "black" : "frozen";
                    if (!placeholderNotified)
                    {
                        placeholderNotified = true;
                        await AddNotificationAsync(recordingId, NotificationKind.PlaceholderDetected, Severity.Warn,
                            $"dead picture ({deadSecs}s {what}); failing over");
                    }
                    _log.LogWarning("[Recorder] Recording {Id} DEAD PICTURE ({Sec}s {What}); killing ffmpeg to fail over", recordingId, deadSecs, what);
                    await GracefulStopAsync(proc, hardKill: true);
                    return new CaptureExit(ExitKind.DeadPicture, $"dead picture ({deadSecs}s {what})");
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), stopToken); } catch (OperationCanceledException) { await GracefulStopAsync(proc); return new CaptureExit(ExitKind.StopRequested, "stop requested"); }
        }
    }

    // Interlocked is wrapped here so the ref-to-field never crosses an await in the async monitor.
    private sealed class Progress
    {
        private long _outTimeUs;
        private long _totalSize;
        public void SetOutTime(long v) => Interlocked.Exchange(ref _outTimeUs, v);
        public void SetTotalSize(long v) => Interlocked.Exchange(ref _totalSize, v);
        public long OutTimeUs => Interlocked.Read(ref _outTimeUs);
        public long TotalSize => Interlocked.Read(ref _totalSize);
    }

    /// <summary>
    /// Parses ffmpeg blackdetect/freezedetect/silencedetect stderr metadata into "active since" wall-clock marks.
    /// Picture death (black/frozen) is the only failover signal; silence is tracked for context but never trips
    /// failover on its own (a quiet passage of play is not a dead feed). Thread-safe (written from the stderr reader,
    /// read from the monitor loop).
    /// </summary>
    private sealed class ContentMonitor
    {
        private long _blackSince, _freezeSince, _silentSince; // wall-clock seconds; 0 = not currently active

        public void Observe(string line, long now)
        {
            // blackdetect:    "black_start:12.3 black_end:18.0 ..."   (note: black_end can appear on the same line)
            // freezedetect:   "lavfi.freezedetect.freeze_start: 12.3" / ".freeze_end: 18.0"
            // silencedetect:  "silence_start: 12.3" / "silence_end: 18.0 | silence_duration: 5.7"
            if (line.Contains("black_end")) Interlocked.Exchange(ref _blackSince, 0);
            else if (line.Contains("black_start")) Interlocked.CompareExchange(ref _blackSince, now, 0);

            if (line.Contains("freeze_end")) Interlocked.Exchange(ref _freezeSince, 0);
            else if (line.Contains("freeze_start")) Interlocked.CompareExchange(ref _freezeSince, now, 0);

            if (line.Contains("silence_end")) Interlocked.Exchange(ref _silentSince, 0);
            else if (line.Contains("silence_start")) Interlocked.CompareExchange(ref _silentSince, now, 0);
        }

        public bool Black => Interlocked.Read(ref _blackSince) != 0;
        public bool Frozen => Interlocked.Read(ref _freezeSince) != 0;
        public bool Silent => Interlocked.Read(ref _silentSince) != 0;

        /// <summary>Seconds the picture has been continuously dead (black or frozen), or 0 if the picture is live.</summary>
        public long DeadPictureSeconds(long now)
        {
            var b = Interlocked.Read(ref _blackSince);
            var f = Interlocked.Read(ref _freezeSince);
            long since = 0;
            if (b != 0) since = b;
            if (f != 0 && (since == 0 || f < since)) since = f;
            return since == 0 ? 0 : Math.Max(0, now - since);
        }
    }

    private static async Task GracefulStopAsync(Process proc, bool hardKill = false)
    {
        if (proc.HasExited) return;
        try
        {
            if (!hardKill)
            {
                // 'q' on stdin is ffmpeg's clean shutdown — finalizes the in-progress segment.
                await proc.StandardInput.WriteAsync('q');
                await proc.StandardInput.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await proc.WaitForExitAsync(cts.Token); return; } catch (OperationCanceledException) { }
            }
            // Stall kills usually need the hammer — a hung network read won't honor 'q'.
            proc.Kill(entireProcessTree: true);
            using var k = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await proc.WaitForExitAsync(k.Token); } catch { }
        }
        catch { try { proc.Kill(entireProcessTree: true); } catch { } }
    }

    private static (long total, int count) MeasureSegments(string segDir)
    {
        long total = 0; var count = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(segDir, "seg-*.ts"))
            {
                try { total += new FileInfo(f).Length; count++; } catch { }
            }
        }
        catch { }
        return (total, count);
    }

    /// <summary>Concat the closed TS segments (lossless -c copy) into the final MKV.</summary>
    private async Task<(bool ok, long bytes, int durationS, string? gaps)> FinalizeAsync(int recordingId, string segDir, string outputPath)
    {
        var segs = Directory.Exists(segDir)
            ? Directory.EnumerateFiles(segDir, "seg-*.ts").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
        // Drop a zero-byte trailing segment (an interrupted open segment).
        segs = segs.Where(f => { try { return new FileInfo(f).Length > 0; } catch { return false; } }).ToList();
        if (segs.Count == 0) return (false, 0, 0, null);

        // Record the captured segments for observability (UI segment count, forensics).
        await _d.Gate.WriteAsync(async () =>
        {
            using var scope = _d.Scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            await db.RecordingSegments.Where(x => x.RecordingId == recordingId).ExecuteDeleteAsync();
            var seq = 0;
            foreach (var f in segs)
            {
                long b = 0; try { b = new FileInfo(f).Length; } catch { }
                db.RecordingSegments.Add(new Data.Entities.RecordingSegment
                {
                    RecordingId = recordingId, Capture = Data.CaptureChain.A, Seq = seq++,
                    Path = f, Bytes = b, Closed = true, ContentVerdict = Data.ContentVerdict.Unverified,
                });
            }
            await db.SaveChangesAsync();
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Build the concat list, DE-OVERLAPPING if enabled. On a continuous (-copyts) timeline, a provider that
        // re-served buffered seconds on reconnect leaves a segment whose PTS regresses below the running timeline:
        // a fully-duplicate segment is dropped outright; a partially-overlapping one starts at an `inpoint` so its
        // duplicate head is skipped. All lossless (-c copy) and a strict improvement over replaying it verbatim
        // (the "going back in time" bug). Falls back to a plain list if PTS can't be read — never worse than before.
        var deoverlap = await GetBoolSettingAsync("finalize_deoverlap_enabled", true);
        var listPath = Path.Combine(segDir, "concat.ffconcat");
        var (listLines, trimmed, dropped, jumpDropped) = await BuildConcatListAsync(segs, deoverlap);
        await File.WriteAllLinesAsync(listPath, listLines);
        if (deoverlap && (trimmed > 0 || dropped > 0 || jumpDropped > 0))
            _log.LogInformation("[Recorder] Recording {Id} de-overlap: {Trim} segment(s) trimmed, {Drop} dropped as duplicates, {Jump} dropped for internal clock jumps", recordingId, trimmed, dropped, jumpDropped);

        var psi = new ProcessStartInfo(_d.Ffmpeg.Ffmpeg)
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-map", "0:v:0", "-map", "0:a:0?",
            // VIDEO stays LOSSLESS copy; the setts BSF rewrites only a rogue >10s PTS outlier (no-op on healthy
            // frames), so a corrupt source timestamp can't inflate the container to bogus hours. DROPPED +genpts:
            // it can't repair a present-but-wrong PTS and extrapolates across source discontinuities.
            "-c:v", "copy", "-bsf:v", "setts=pts=if(gt(abs(PTS-DTS)\\,900000)\\,DTS\\,PTS)",
            // AUDIO is re-encoded to ONE uniform AAC track (cheap CPU, NO NVENC). IPTV pre-show vs main feed
            // frequently use different audio codecs/SBR configs; concatenating them under -c copy leaves ~1 min
            // undecodable (silent) at the splice. A uniform re-encode eliminates that whole class of audio dropouts.
            // aresample async keeps audio locked to the (de-overlapped) video timeline across reconnect splices
            // instead of leaving a silent gap; make_zero rebases any small negative TS the inpoint seeks introduce.
            "-c:a", "aac", "-b:a", "256k", "-af", "aresample=async=1:min_hard_comp=0.1",
            "-avoid_negative_ts", "make_zero",
            outputPath,
        }) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync() is not null) { } } catch { } });
            _ = Task.Run(async () => { try { while (await p.StandardOutput.ReadLineAsync() is not null) { } } catch { } });
            // Scale the hang-guard by footage length (≈75 8s-segments per 10 min) so a long motorsport finalize
            // can't be clipped, while still bounding a wedged ffmpeg. Copy-video + CPU-AAC runs well faster than
            // realtime, so this is generous: ~10 min floor, ~60 min ceiling.
            var capMin = Math.Clamp(10 + segs.Count / 10, 10, 60);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(capMin));
            await p.WaitForExitAsync(cts.Token);
            if (p.ExitCode != 0 || !File.Exists(outputPath)) return (false, 0, 0, null);
            var bytes = new FileInfo(outputPath).Length;
            var durationS = await ProbeDurationAsync(outputPath);
            return (bytes > 0, bytes, durationS, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Recorder] Finalize concat failed for {Id}", recordingId);
            return (false, 0, 0, null);
        }
    }

    /// <summary>Resolve a boolean setting from a fresh scope (finalize can run from boot-recovery, with no ambient settings).</summary>
    private async Task<bool> GetBoolSettingAsync(string key, bool fallback)
    {
        try
        {
            using var scope = _d.Scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<DVarr.Services.SettingsService>();
            var v = await settings.GetAsync(key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            return v.Trim() is "true" or "1" or "yes" or "on";
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Build the ffconcat lines (header + file/inpoint directives). When <paramref name="deoverlap"/> is on, probe
    /// each segment's PTS range and skip duplicated content re-served on reconnect: a fully-duplicate segment is
    /// dropped; a partial overlap gets an `inpoint` at the running timeline end so only the new tail is kept.
    /// A segment whose INTERNAL span is impossibly long is dropped too (see <see cref="MaxSaneSegmentSpanS"/>).
    /// </summary>
    private async Task<(List<string> lines, int trimmed, int dropped, int jumpDropped)> BuildConcatListAsync(List<string> segs, bool deoverlap)
    {
        static string FileLine(string s) => $"file '{s.Replace("\\", "/").Replace("'", "'\\''")}'";
        var lines = new List<string> { "ffconcat version 1.0" };
        if (!deoverlap) { lines.AddRange(segs.Select(FileLine)); return (lines, 0, 0, 0); }

        const double eps = 0.05;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double timelineEnd = double.NegativeInfinity;
        int trimmed = 0, dropped = 0, jumpDropped = 0;
        foreach (var s in segs)
        {
            var (min, max, ok) = await ProbePtsRangeAsync(s);
            if (!ok)
            {
                // Unreadable PTS → include verbatim and reset the guard (never worse than the old behaviour).
                lines.Add(FileLine(s));
                timelineEnd = double.NegativeInfinity;
            }
            else if (max - min > MaxSaneSegmentSpanS)
            {
                // Intra-segment clock jump (bug #7 residual): ffmpeg's own -reconnect splices the provider's NEW
                // connection into the SAME segment file, and if that connection restarted its PCR/PTS clock the jump
                // sits INSIDE one file — the setts BSF misses it (PTS and DTS jump together) and the concat demuxer
                // only re-bases at file boundaries, so it survived into the MKV (players see a ~20h file and stall at
                // the seam). An 8s clock-cut segment can span a GOP or two more, never minutes — drop the straddling
                // file (≤ ~8s of footage at the glitch) and the neighbours join at a boundary concat re-bases cleanly.
                // timelineEnd is left as-is: the next clean segment (on the jumped clock) simply appends after it.
                jumpDropped++;
                _log.LogWarning("[Recorder] segment {Seg} spans {Span:0.#}s internally (clock jump inside the file) — dropped from finalize", Path.GetFileName(s), max - min);
            }
            else if (double.IsNegativeInfinity(timelineEnd) || min >= timelineEnd - eps)
            {
                lines.Add(FileLine(s));                       // no overlap
                timelineEnd = max;
            }
            else if (max <= timelineEnd + eps)
            {
                dropped++;                                    // wholly inside the timeline already → drop the file
            }
            else
            {
                lines.Add(FileLine(s));                       // partial overlap → skip the duplicate head
                lines.Add($"inpoint {timelineEnd.ToString(inv)}");
                trimmed++;
                timelineEnd = max;
            }
        }
        // Pathological source (e.g. every segment straddles a PCR wrap): if the jump filter would leave NOTHING,
        // fall back to the plain list — a file with a weird timeline still beats no recording at all.
        if (jumpDropped > 0 && !lines.Skip(1).Any())
        {
            lines = new List<string> { "ffconcat version 1.0" };
            lines.AddRange(segs.Select(FileLine));
            return (lines, 0, 0, 0);
        }
        return (lines, trimmed, dropped, jumpDropped);
    }

    /// <summary>Longest internal PTS span (seconds) a single clock-cut segment can legitimately have. Segments are
    /// cut every 8s of wall clock (plus up to a GOP), so minutes of internal span always means a mid-file clock jump.</summary>
    private const double MaxSaneSegmentSpanS = 300;

    /// <summary>Min &amp; max video packet PTS (seconds) of a segment via ffprobe (demux only, no decode). Min/max
    /// (not first/last) so an intra-segment reconnect that itself regressed PTS doesn't fool the overlap maths.</summary>
    private async Task<(double min, double max, bool ok)> ProbePtsRangeAsync(string seg)
    {
        try
        {
            var psi = new ProcessStartInfo(_d.Ffmpeg.Ffprobe)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "quiet", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", seg })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var outp = await p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(cts.Token);
            double min = double.PositiveInfinity, max = double.NegativeInfinity; var any = false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // ffprobe 7/8's csv writer appends a trailing comma when the packet carries side data
                // ("1.423222,"), which silently failed the parse and no-op'd the whole de-overlap pass.
                var cell = line.Trim().TrimEnd(',');
                if (double.TryParse(cell, System.Globalization.NumberStyles.Float, inv, out var v))
                { if (v < min) min = v; if (v > max) max = v; any = true; }
            }
            return any ? (min, max, true) : (0, 0, false);
        }
        catch { return (0, 0, false); }
    }

    private async Task<int> ProbeDurationAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(_d.Ffmpeg.Ffprobe)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "quiet", "-show_entries", "format=duration", "-of", "default=nk=1:nw=1", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return double.TryParse(outp.Trim(), out var d) ? (int)d : 0;
        }
        catch { return 0; }
    }

    // ----- state persistence + SSE -----

    /// <summary>Re-read the recording's current window end (EndUtc + PostPadS) from the DB — a fresh short
    /// AsNoTracking scope, same pattern as <see cref="UpdateProgressAsync"/>. Returns <paramref name="fallback"/>
    /// (the last-known window) on a missing row or ANY read error, so a transient DB hiccup can never shrink or
    /// abort a live capture. Callers additionally floor the result at the initial window (see RunAsync).</summary>
    private async Task<long> ReadWindowEndAsync(int recordingId, long fallback)
    {
        try
        {
            using var scope = _d.Scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var row = await db.Recordings.AsNoTracking()
                .Where(r => r.Id == recordingId)
                .Select(r => new { r.EndUtc, r.PostPadS })
                .FirstOrDefaultAsync();
            return row is null ? fallback : row.EndUtc + row.PostPadS;
        }
        catch { return fallback; }
    }

    private async Task UpdateProgressAsync(int recordingId, long bytes, long? contentOkUtc = null)
    {
        await _d.Gate.WriteAsync(async () =>
        {
            using var scope = _d.Scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var r = await db.Recordings.FindAsync(recordingId);
            if (r is null) return;
            r.BytesWritten = bytes;
            r.LastFrameUtc = EpochTime.Now();
            if (contentOkUtc is { } co) r.LastContentOkUtc = co;
            r.UpdatedUtc = EpochTime.Now();
            await db.SaveChangesAsync();
        });
        Publish(recordingId, RecordingState.Recording, bytes);
    }

    /// <summary>Write a standalone notification (no state change) — used for advisory events like dead-picture detection.</summary>
    private async Task AddNotificationAsync(int recordingId, NotificationKind kind, Severity sev, string message)
    {
        try
        {
            await _d.Gate.WriteAsync(async () =>
            {
                using var scope = _d.Scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                db.Notifications.Add(new Notification
                {
                    RecordingId = recordingId, TsUtc = EpochTime.Now(), Kind = kind, Severity = sev, Message = message,
                });
                await db.SaveChangesAsync();
            });
        }
        catch (Exception ex) { _log.LogDebug(ex, "[Recorder] notification write failed for {Id}", recordingId); }
    }

    private async Task SetStateAsync(int recordingId, RecordingState state, Action<RecordingEntity>? mutate = null,
        NotificationKind? notify = null, Severity sev = Severity.Info, string? message = null, bool persist = true)
    {
        long bytes = 0;
        string? from = null;
        if (persist)
        {
            await _d.Gate.WriteAsync(async () =>
            {
                using var scope = _d.Scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                var r = await db.Recordings.FindAsync(recordingId);
                if (r is null) return;
                from = r.State.ToString();
                r.State = state;
                r.UpdatedUtc = EpochTime.Now();
                mutate?.Invoke(r);
                bytes = r.BytesWritten;
                if (notify is { } k)
                {
                    db.Notifications.Add(new Notification
                    {
                        RecordingId = recordingId, TsUtc = EpochTime.Now(), Kind = k, Severity = sev,
                        FromState = from, ToState = state.ToString(), Message = message,
                    });
                }
                await db.SaveChangesAsync();
            });
            _log.LogInformation("[Recorder] Recording {Id}: {From} → {State}{Msg}", recordingId, from, state, message is null ? "" : $" ({message})");
        }
        Publish(recordingId, state, bytes, message);
    }

    private void Publish(int recordingId, RecordingState state, long bytes, string? message = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = recordingId,
            state = state.ToString(),
            bytes,
            ts = EpochTime.Now(),
            message,
        });
        _d.Bus.Publish(payload);
    }
}
