using System.Collections.Concurrent;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Ingest;
using DVarr.Services.Tuner;
using Microsoft.EntityFrameworkCore;
using RecordingEntity = DVarr.Data.Entities.Recording;

namespace DVarr.Services.Recording;

/// <summary>
/// Owns the live supervisors. Resolves the stream URL, takes the credential's single tuner
/// slot, launches a <see cref="RecorderSupervisor"/> per recording, and handles stop and
/// boot recovery. The recordings table is the source of truth (docs/05 §3). Singleton.
/// </summary>
public sealed class RecorderService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DbWriteGate _gate;
    private readonly FfmpegLocator _ffmpeg;
    private readonly TunerLeaseManager _tuner;
    private readonly RecordingEventBus _bus;
    private readonly ILoggerFactory _lf;
    private readonly ILogger<RecorderService> _log;
    private readonly RuntimePaths _paths;

    private readonly ConcurrentDictionary<int, (Task task, CancellationTokenSource cts)> _active = new();

    private static readonly RecordingState[] NonTerminal =
    {
        RecordingState.Pending, RecordingState.Starting, RecordingState.Recording,
        RecordingState.Recovering, RecordingState.FailingOver, RecordingState.Degraded,
        RecordingState.Stopping, RecordingState.Finalizing,
    };

    public RecorderService(IServiceScopeFactory scopes, DbWriteGate gate, FfmpegLocator ffmpeg,
        TunerLeaseManager tuner, RecordingEventBus bus, ILoggerFactory lf, ILogger<RecorderService> log, RuntimePaths paths)
    {
        _scopes = scopes; _gate = gate; _ffmpeg = ffmpeg; _tuner = tuner;
        _bus = bus; _lf = lf; _log = log; _paths = paths;
    }

    public bool IsActive(int id) => _active.ContainsKey(id);
    public IReadOnlyCollection<int> ActiveIds => _active.Keys.ToArray();

    /// <summary>Resolve, acquire the credential slot, and launch the supervisor. Returns null on success or a reason string.</summary>
    public async Task<string?> TryStartAsync(int recordingId, CancellationToken stoppingToken)
    {
        // ATOMIC start guard (#1): reserve the id in _active BEFORE any async work, so two concurrent start calls
        // (scheduler tick + manual /start) for the same recording can't both pass a check-then-act and launch two
        // supervisors / hold two leases. The real linked cts is reserved now (cancellation works during setup); on
        // success the placeholder is swapped for the running task, on ANY failure the reservation is removed (finally).
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_active.TryAdd(recordingId, (Task.CompletedTask, cts))) { cts.Dispose(); return "already running"; }
        var started = false;
        try
        {
        string url, segDir, outputPath;
        long windowEnd;
        int stall, contentDeadTimeout, contentVerifyFps;
        bool nativeRate, contentVerify, cleanEof;
        string contentVerifyHwaccel;
        TunerLease lease;
        List<(int channelId, int streamId, string url)> fallbacks;

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var xtream = scope.ServiceProvider.GetRequiredService<XtreamClient>();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

            var rec = await db.Recordings.FindAsync(recordingId);
            if (rec is null) return "recording not found";

            var src = await db.Sources.FindAsync(rec.SourceId);
            var ch = await db.Channels.FindAsync(rec.ChannelId);
            if (src is null || ch is null) return "source/channel missing";
            // HARD GUARD: a disabled source is off-limits — never contact the provider for it, even from the
            // background auto-record pipeline. This is the structural enforcement of the "don't touch Source 1" rule.
            if (!src.Enabled) return $"source '{src.Label}' is disabled — refusing to contact it";

            // Acquire the credential's single slot. If THIS recording's credential is busy, SPREAD to the same
            // logical channel on another enabled login that has a free slot (conflict planning, bug #7). This is what
            // makes the schedule-modal "will record on <other login>" badge actually happen — manual recordings are
            // otherwise pinned to one source with no fallbacks, so an overlap just sat in Pending until it Missed.
            TunerLease? acquired = await _tuner.TryAcquireAsync(rec.SourceId, LeasePurpose.Recording, recordingId, rec.ChannelId, rec.StreamId, stoppingToken);
            if (acquired is null)
            {
                var resolver = scope.ServiceProvider.GetRequiredService<DVarr.Services.Events.ResolverService>();
                foreach (var eq in await resolver.EquivalentChannelsAsync(rec.ChannelId, stoppingToken))
                {
                    var eqSrc = await db.Sources.FindAsync(eq.SourceId);
                    if (eqSrc is null || !eqSrc.Enabled) continue;
                    var spread = await _tuner.TryAcquireAsync(eq.SourceId, LeasePurpose.Recording, recordingId, eq.ChannelId, eq.StreamId, stoppingToken);
                    if (spread is null) continue;
                    // The spread credential's single slot is now HELD. This block is OUTSIDE the lease-release try
                    // below (it runs before `lease = acquired`), so any throw here would leak that login's slot until
                    // restart — guard it and release on failure.
                    try
                    {
                        // Re-home: persist the new credential/channel so the UI + finalize reflect reality. Fallbacks are
                        // pinned to the old SourceId by the composite FK, so a credential change drops them.
                        var fromLabel = src.Label;
                        await _gate.WriteAsync(async () =>
                        {
                            // SourceId is part of the (Id, SourceId) alternate key, so it can't be changed on the tracked
                            // entity (EF rejects it) — re-point via RecordingRepoint (deletes fallbacks + bypasses the tracker).
                            var now = EpochTime.Now();
                            await RecordingRepoint.ApplyAsync(db, recordingId, eq.SourceId, eq.ChannelId, eq.StreamId, now);
                            db.Notifications.Add(new Notification { RecordingId = recordingId, TsUtc = now, Kind = NotificationKind.FailedOver, Severity = Severity.Info, Message = $"credential '{fromLabel}' busy → recording on '{eqSrc.Label}'" });
                            await db.SaveChangesAsync();
                            // ExecuteUpdate bypassed the tracker, so the loaded `rec` is stale (and its alt-key SourceId
                            // changed in the DB — Reload() would itself throw the "can't modify a key" error). Detach it
                            // so the FindAsync just below re-queries the fresh row.
                            db.Entry(rec).State = EntityState.Detached;
                        });
                        var rrec = await db.Recordings.FindAsync(recordingId);
                        var rch = await db.Channels.FindAsync(eq.ChannelId);
                        if (rrec is null || rch is null) { await _tuner.ReleaseAsync(spread); continue; } // re-homed row/channel vanished — give the slot back, try next
                        rec = rrec; src = eqSrc; ch = rch; acquired = spread;
                        _log.LogInformation("[Recorder] Recording {Id}: primary credential busy → spread to '{Label}' (channel {Ch})", recordingId, eqSrc.Label, eq.ChannelId);
                        break;
                    }
                    catch
                    {
                        await _tuner.ReleaseAsync(spread); // never leak the spread login's only slot on a re-home failure
                        throw;
                    }
                }
                if (acquired is null) return $"credential '{src.Label}' is busy and no equivalent login has a free slot (1 stream/login)";
            }
            lease = acquired;

            // The credential slot is now HELD. Any failure before the supervisor owns the lease MUST
            // release it, or that single-stream credential is dead for the rest of the process lifetime.
            try
            {
                url = ResolveUrl(src, ch, xtream);
                nativeRate = !string.IsNullOrWhiteSpace(ch.DirectUrl);
                segDir = Path.Combine(_paths.SegmentDir, recordingId.ToString(), "A");
                outputPath = BuildOutputPath(rec, ch);
                windowEnd = rec.EndUtc + rec.PostPadS;
                stall = await settings.GetIntAsync("segment_no_progress_timeout_s");
                if (stall <= 0) stall = 25;
                contentVerify = await settings.GetBoolAsync("content_verify_enabled");
                contentDeadTimeout = await settings.GetIntAsync("content_dead_timeout_s");
                if (contentDeadTimeout <= 0) contentDeadTimeout = 30;
                // The dead-feed decode runs on the GPU (NVDEC) and samples only a few fps, so it costs almost no CPU.
                // hwaccel "" / "none" → software decode; fps 0 → every frame.
                contentVerifyHwaccel = (await settings.GetAsync("content_verify_hwaccel"))?.Trim() ?? "";
                contentVerifyFps = await settings.GetIntAsync("content_verify_fps");
                // Clean rc=0 EOFs (a momentary line drop) relaunch instantly without Recovering churn; off → treat
                // them like any other recoverable fault (back-off + failover ladder).
                cleanEof = await settings.GetBoolAsync("clean_eof_instant_relaunch");

                // Pre-load same-credential fallbacks (rank 2..N; rank 1 is the primary on Recording.ChannelId) and
                // resolve their URLs. The supervisor walks this ladder in order when the primary dies or goes dead.
                var fbRows = await db.RecordingFallbacks.Where(f => f.RecordingId == recordingId && f.Rank >= 2).OrderBy(f => f.Rank).ToListAsync(stoppingToken);
                fallbacks = new();
                foreach (var fb in fbRows)
                {
                    var fch = await db.Channels.FindAsync(fb.ChannelId);
                    var fsrc = await db.Sources.FindAsync(fb.SourceId);
                    if (fch is not null && fsrc is not null)
                        fallbacks.Add((fch.Id, fch.StreamId, ResolveUrl(fsrc, fch, xtream)));
                }

                var fbIndex = 0;
                Func<int, Task<(int channelId, int streamId, string url)?>> next = _ =>
                    fbIndex < fallbacks.Count
                        ? Task.FromResult<(int, int, string)?>(fallbacks[fbIndex++])
                        : Task.FromResult<(int, int, string)?>(null);

                var sup = new RecorderSupervisor(new RecorderSupervisor.Deps(_scopes, _gate, _ffmpeg, _tuner, _bus, _lf));
                var task = Task.Run(() => sup.RunAsync(recordingId, url, segDir, outputPath, windowEnd, stall, nativeRate, contentVerify, contentDeadTimeout, contentVerifyHwaccel, contentVerifyFps, cleanEof, src?.UserAgent, lease, next, cts.Token), CancellationToken.None);
                _active[recordingId] = (task, cts); // swap the reservation placeholder for the running task (same cts)
                _ = task.ContinueWith(t => { _active.TryRemove(recordingId, out _); cts.Dispose(); }, TaskScheduler.Default);
                started = true;

                _log.LogInformation("[Recorder] Started recording {Id} on '{Url}' (window ends {End})", recordingId, Mask(url), windowEnd);
                return null;
            }
            catch (Exception ex)
            {
                await _tuner.ReleaseAsync(lease); // never leak the single credential slot
                _log.LogError(ex, "[Recorder] Failed to start recording {Id}; released slot", recordingId);
                return "start failed: " + ex.Message;
            }
        }
        }
        finally
        {
            // Any non-success path (early return, busy, or setup exception) frees the reservation + cts. On success
            // started=true and the running task owns the cts (disposed by its ContinueWith), so this is a no-op.
            if (!started) { _active.TryRemove(recordingId, out _); cts.Dispose(); }
        }
    }

    /// <summary>Cancel an active recording and wait (bounded) for the supervisor to fully unwind — finalize,
    /// persist/abandon segments, and release the tuner lease. Returns true if it actually SETTLED within the wait
    /// (so callers like delete can avoid removing the row mid-finalize). Returns true immediately if not active.</summary>
    public async Task<bool> StopAsync(int recordingId)
    {
        if (!_active.TryGetValue(recordingId, out var entry)) return true; // nothing running → already settled
        _log.LogInformation("[Recorder] Stop requested for recording {Id}", recordingId);
        try { entry.cts.Cancel(); } catch { }
        // Capture stops within seconds; finalize (concat + AAC) can legitimately run minutes for a long recording,
        // so report whether it actually completed in the wait window rather than assuming "settled".
        try { await entry.task.WaitAsync(TimeSpan.FromSeconds(60)); } catch { }
        return entry.task.IsCompleted;
    }

    /// <summary>Boot recovery (docs/05 §3.4): resume open windows; mark fully-passed non-terminal rows MISSED.</summary>
    public async Task ResumeOrRecoverAsync(CancellationToken stoppingToken)
    {
        await _tuner.ReconcileOnBootAsync(stoppingToken);

        List<RecordingEntity> rows;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            rows = await db.Recordings.Where(r => NonTerminal.Contains(r.State)).ToListAsync(stoppingToken);
        }

        var now = EpochTime.Now();
        foreach (var r in rows)
        {
            var winStart = r.StartUtc - r.PrePadS;
            var winEnd = r.EndUtc + r.PostPadS;
            if (now >= winStart && now < winEnd)
            {
                var err = await TryStartAsync(r.Id, stoppingToken);
                if (err is not null) _log.LogWarning("[Recorder] Resume of {Id} deferred: {Err}", r.Id, err);
            }
            else if (now >= winEnd)
            {
                // The window passed during downtime. If segments survived on disk (esp. a crash mid-finalize),
                // re-finalize them rather than throwing the capture away (docs/05 §3.4). MISSED only if nothing exists.
                var segDir = Path.Combine(_paths.SegmentDir, r.Id.ToString(), "A");
                if (r.State is RecordingState.Finalizing or RecordingState.Stopping || HasSegments(segDir))
                {
                    _log.LogInformation("[Recorder] Re-finalizing recording {Id} from surviving segments after restart", r.Id);
                    try { await ReFinalizeAsync(r); }
                    catch (Exception ex) { _log.LogError(ex, "[Recorder] Re-finalize of {Id} failed", r.Id); await MarkMissedAsync(r.Id, "re-finalize failed: " + ex.Message); }
                }
                else
                {
                    await MarkMissedAsync(r.Id, "window elapsed while DVarr was down (no segments captured)");
                }
            }
            // future windows stay PENDING for the scheduler to arm.
        }
    }

    public async Task MarkMissedAsync(int recordingId, string why)
    {
        var now = EpochTime.Now();
        await _gate.WriteAsync(async () =>
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var r = await db.Recordings.FindAsync(recordingId);
            if (r is null || !NonTerminal.Contains(r.State)) return;
            r.State = RecordingState.Missed;
            r.UpdatedUtc = now;
            r.FailureReason = why;
            db.Notifications.Add(new Notification
            {
                RecordingId = recordingId, TsUtc = now, Kind = NotificationKind.Missed,
                Severity = Severity.Critical, ToState = "Missed", Message = why,
            });
            await db.SaveChangesAsync();
        });
        _log.LogWarning("[Recorder] Recording {Id} MISSED: {Why}", recordingId, why);
    }

    /// <summary>Re-finalize a recording from segments that survived a restart (no lease needed — process restarted).</summary>
    private async Task ReFinalizeAsync(RecordingEntity r)
    {
        var segDir = Path.Combine(_paths.SegmentDir, r.Id.ToString(), "A");
        var outputPath = !string.IsNullOrWhiteSpace(r.OutputPath) ? r.OutputPath! : Path.Combine(_paths.MediaDir, $"Recording {r.Id}.mkv");
        var sup = new RecorderSupervisor(new RecorderSupervisor.Deps(_scopes, _gate, _ffmpeg, _tuner, _bus, _lf));
        await sup.FinalizeToTerminalAsync(r.Id, segDir, outputPath);
    }

    private static bool HasSegments(string segDir)
    {
        try
        {
            return Directory.Exists(segDir) &&
                   Directory.EnumerateFiles(segDir, "seg-*.ts").Any(f => { try { return new FileInfo(f).Length > 0; } catch { return false; } });
        }
        catch { return false; }
    }

    private static string ResolveUrl(ProviderSource src, Channel ch, XtreamClient xtream)
        => !string.IsNullOrWhiteSpace(ch.DirectUrl) ? ch.DirectUrl! : xtream.StreamTsUrl(src, ch.StreamId);

    private string BuildOutputPath(RecordingEntity rec, Channel ch)
    {
        var title = !string.IsNullOrWhiteSpace(rec.Title) ? rec.Title! : $"Recording {rec.Id}";
        var stamp = EpochTime.ToBrisbane(rec.StartUtc).ToString("yyyy-MM-dd_HHmm");
        var name = $"{Sanitize(title)} [{stamp}].mkv";
        return Path.Combine(_paths.MediaDir, name);
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Join("_", s.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrEmpty(cleaned) ? "recording" : cleaned;
    }

    private static string Mask(string url)
    {
        // hide credentials embedded in an Xtream /live/user/pass/ URL when logging
        try
        {
            var u = new Uri(url);
            var segs = u.AbsolutePath.Split('/');
            if (segs.Length >= 4 && segs[1] == "live") { segs[2] = "***"; segs[3] = "***"; }
            return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{string.Join('/', segs)}";
        }
        catch { return url; }
    }
}
