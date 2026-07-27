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
        int stall, contentDeadTimeout, contentVerifyFps, bitrateFloor;
        bool nativeRate, finiteInput, contentVerify, cleanEof;
        Func<Task<string?>>? nextChunk;
        Func<int>? chunkDur;
        string contentVerifyHwaccel;
        TunerLease lease;
        List<(int channelId, int streamId, string url)> fallbacks;

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var xtream = scope.ServiceProvider.GetRequiredService<XtreamClient>();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

            // Final EPG re-pick against the freshest guide, BEFORE the recording row is loaded/leased — if another
            // mapped channel's guide shows the event, the row is re-pointed (same credential) and the load below sees
            // the new channel. No-op for manual recordings, locked channels, or when the feature is off.
            try { await scope.ServiceProvider.GetRequiredService<DVarr.Services.Events.EpgRepickService>().TryRepickAsync(recordingId, stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[Recorder] arm-time EPG re-pick failed for {Id} — recording on the planned channel", recordingId); }

            var rec = await db.Recordings.FindAsync(recordingId);
            if (rec is null) return "recording not found";
            // Terminal rows must never arm: the arm path's later state writes (Starting/Recording) would blindly
            // overwrite e.g. a Cancelled written between the caller's due-query and here (real case: the rescue
            // settle pass cancelling an orphaned replay while the scheduler was mid-arm on the same row).
            if (rec.State is RecordingState.Done or RecordingState.Cancelled or RecordingState.Missed or RecordingState.NeedsAttention)
                return $"recording is {rec.State} — not arming";

            var src = await db.Sources.FindAsync(rec.SourceId);
            var ch = await db.Channels.FindAsync(rec.ChannelId);
            if (src is null || ch is null) return "source/channel missing";
            // HARD GUARD: a disabled source is off-limits — never contact the provider for it, even from the
            // background auto-record pipeline. This is the structural enforcement of the "don't touch Source 1" rule.
            if (!src.Enabled) return $"source '{src.Label}' is disabled — refusing to contact it";

            // Global concurrency cap: DVarr won't run more than max_global_concurrent_recordings at once across ALL
            // logins (bounds CPU/disk/network beyond the per-credential 1-stream limit). _active already holds this
            // recording's own reservation (added above), so subtract it. Over the cap → stay Pending and retry next
            // tick, exactly like a busy credential — never a hard failure. 0/blank = no global cap.
            var maxConcurrent = await settings.GetIntAsync("max_global_concurrent_recordings");
            if (maxConcurrent > 0 && _active.Count - 1 >= maxConcurrent)
                return $"at the global limit of {maxConcurrent} simultaneous recording(s) — will start when one frees";

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
                // Live-preempts-opportunistic: every login is busy, but if the one holding THIS recording's
                // credential is only running an Opportunistic capture (a rescue replay or a catch-up pull), a
                // live game outranks it — a replay can always be re-hunted; the live broadcast cannot. The victim
                // is stopped WITHOUT finalizing (AbandonRequested → Cancelled, scratch discarded) so its partial
                // copy can't satisfy the rescue "good copy" check; the rescue settle pass then re-opens its
                // ticket and hunts again (archive pulls simply re-run later).
                if (acquired is null && rec.Priority != RecordingPriority.Opportunistic
                    && await settings.GetBoolAsync("live_preempts_opportunistic"))
                {
                    var activeIds = ActiveIds.ToList();
                    var victim = await db.Recordings.AsNoTracking()
                        .Where(v => activeIds.Contains(v.Id) && v.Id != recordingId && v.SourceId == rec.SourceId
                                    && v.Priority == RecordingPriority.Opportunistic
                                    && (v.State == RecordingState.Starting || v.State == RecordingState.Recording
                                        || v.State == RecordingState.Recovering || v.State == RecordingState.FailingOver
                                        || v.State == RecordingState.Degraded))
                        .Select(v => new { v.Id, v.Title, v.CatchupSourceStartUtc })
                        .FirstOrDefaultAsync(stoppingToken);
                    if (victim is not null)
                    {
                        var kind = victim.CatchupSourceStartUtc is not null ? "catch-up download" : "replay";
                        _log.LogWarning("[Recorder] Recording {Id}: preempting Opportunistic {Kind} {Vid} '{VTitle}' on '{Label}' — a live recording needs the slot",
                            recordingId, kind, victim.Id, victim.Title, src.Label);
                        RecorderSupervisor.AbandonRequested[victim.Id] = $"preempted — live recording '{rec.Title}' needs this login (the {kind} will be retried)";
                        await _gate.WriteAsync(async () =>
                        {
                            db.Notifications.Add(new Notification
                            {
                                RecordingId = recordingId, TsUtc = EpochTime.Now(), Kind = NotificationKind.FailedOver, Severity = Severity.Info,
                                Message = $"preempted the {kind} of “{victim.Title}” to free '{src.Label}' for this live recording",
                            });
                            await db.SaveChangesAsync();
                        });
                        var settled = await StopAsync(victim.Id);
                        // If the victim's supervisor finished on its own before consuming the abandon flag (raced a
                        // natural completion), the stale entry would wrongly cancel that recording's NEXT run. Once
                        // the task has settled the flag is either consumed (gone) or unreachable — safe to drop.
                        // While NOT settled the supervisor may still be heading for the check, so the flag stays.
                        // (The abandon path itself re-queues a preempted MANUAL catch-up pull to Pending; ticket-
                        // linked captures go Cancelled and the rescue settle pass owns their retry.)
                        if (settled)
                        {
                            RecorderSupervisor.AbandonRequested.TryRemove(victim.Id, out _);
                            var post = await db.Recordings.AsNoTracking().Where(v => v.Id == victim.Id).Select(v => v.State).FirstOrDefaultAsync(stoppingToken);
                            if (post == RecordingState.Done)
                                _log.LogWarning("[Recorder] preempt of {Vid} raced its natural completion — it finalized Done before the abandon took effect", victim.Id);
                        }
                        // The victim's lease releases as soon as its capture stops (before any finalize) — poll
                        // briefly for the freed slot rather than waiting a whole scheduler tick.
                        for (var w = 0; w < 30 && acquired is null && !stoppingToken.IsCancellationRequested; w++)
                        {
                            acquired = await _tuner.TryAcquireAsync(rec.SourceId, LeasePurpose.Recording, recordingId, rec.ChannelId, rec.StreamId, stoppingToken);
                            if (acquired is null) await Task.Delay(500, stoppingToken);
                        }
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
                finiteInput = nativeRate;
                nextChunk = null;
                chunkDur = null;
                int catchupShapeIdx = 0;
                List<string>? catchupShapes = null;
                Func<int, string>? catchupUrlFor = null;
                int catchupChunkIdx = 0, catchupChunkCount = 0;
                if (rec.CatchupSourceStartUtc is { } catchupStart && rec.CatchupDurationS is { } catchupDur && catchupDur > 0)
                {
                    // CATCH-UP pull: read the provider's tv_archive instead of the live stream. Finite input, no
                    // rate throttle (the whole point is faster-than-realtime), chunked so providers that cap a
                    // single timeshift request still serve the whole game. The provider's archive URL shape is
                    // probed via the failover ladder: known/preferred shape first, the alternate as the "fallback".
                    var chunkMin = await settings.GetIntAsync("catchup_chunk_minutes");
                    var chunkS = Math.Clamp(chunkMin <= 0 ? 60 : chunkMin, 5, 240) * 60;
                    catchupShapes = src.CatchupShape == XtreamClient.ShapeTimeshiftPath
                        ? new List<string> { XtreamClient.ShapeTimeshiftPath, XtreamClient.ShapeTimeshiftPhp }
                        : new List<string> { XtreamClient.ShapeTimeshiftPhp, XtreamClient.ShapeTimeshiftPath };
                    catchupChunkCount = (catchupDur + chunkS - 1) / chunkS;
                    var srcSnapshot = src; var chSnapshot = ch;
                    // Convert the pull's base start to provider-local ONCE and offset chunks linearly from it —
                    // per-chunk conversion would collide two chunks onto one stamp across a DST fall-back hour.
                    var baseLocal = XtreamClient.ToProviderLocal(src, catchupStart);
                    int ChunkDur(int idx) => (int)Math.Min(chunkS, catchupDur - (long)idx * chunkS);
                    catchupUrlFor = idx =>
                        xtream.TimeshiftUrlLocal(srcSnapshot, chSnapshot.StreamId, baseLocal.AddSeconds((long)idx * chunkS), ChunkDur(idx), catchupShapes[catchupShapeIdx]);
                    url = catchupUrlFor(0);
                    chunkDur = () => ChunkDur(catchupChunkIdx);
                    nativeRate = false;
                    finiteInput = true;
                    // A fresh pull always starts from chunk 0 — stale segments from an interrupted earlier run of
                    // this same recording (boot recovery, preempt requeue) would duplicate everything re-pulled.
                    var staleScratch = Path.Combine(_paths.SegmentDir, recordingId.ToString());
                    try { if (Directory.Exists(staleScratch)) Directory.Delete(staleScratch, recursive: true); }
                    catch (Exception exWipe) { _log.LogWarning(exWipe, "[Recorder] couldn't clear stale catch-up scratch for {Id}", recordingId); }
                    var srcId = src.Id;
                    nextChunk = async () =>
                    {
                        // A chunk just finished cleanly → the current shape WORKS on this provider; remember it so
                        // future pulls (and the UI) start on the right shape without re-probing. Best-effort.
                        var provenShape = catchupShapes[catchupShapeIdx];
                        try
                        {
                            using var shapeScope = _scopes.CreateScope();
                            var sdb = shapeScope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                            await _gate.WriteAsync(async () =>
                            {
                                var srow = await sdb.Sources.FindAsync(srcId);
                                if (srow is not null && srow.CatchupShape != provenShape)
                                { srow.CatchupShape = provenShape; srow.UpdatedUtc = EpochTime.Now(); await sdb.SaveChangesAsync(); }
                            });
                        }
                        catch { /* shape memo is a nicety — never disturb the pull */ }
                        return ++catchupChunkIdx < catchupChunkCount ? catchupUrlFor(catchupChunkIdx) : null;
                    };
                }
                segDir = Path.Combine(_paths.SegmentDir, recordingId.ToString(), "A");
                outputPath = BuildOutputPath(rec, ch);
                // INITIAL window only — the supervisor re-reads EndUtc + PostPadS live while capturing, so a smart
                // auto-stop extension (AutoStopService mutating Recording.EndUtc) takes effect mid-recording.
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

                // Bitrate-floor placeholder detection (opt-in): resolve THIS channel's floor from its quality tier.
                // 0 = disabled (feature off, or an unknown tier we choose not to police). Passed to the supervisor,
                // which fails over when the rolling stream bitrate stays below it — a provider slate feeds bytes but
                // far too few for real content, which the picture-based dead-feed check needs a GPU to catch.
                bitrateFloor = 0;
                if (await settings.GetBoolAsync("bitrate_floor_enabled"))
                {
                    var sd = await settings.GetIntAsync("bitrate_floor_kbps_sd"); if (sd <= 0) sd = 400;
                    var hd = await settings.GetIntAsync("bitrate_floor_kbps_hd"); if (hd <= 0) hd = 800;
                    var uhd = await settings.GetIntAsync("bitrate_floor_kbps_uhd"); if (uhd <= 0) uhd = 2000;
                    bitrateFloor = ch.DetectedQuality switch { "2160p" => uhd, "1080p" or "720p" => hd, _ => sd };
                }

                Func<int, Task<(int channelId, int streamId, string url)?>> next;
                if (catchupUrlFor is not null)
                {
                    // Catch-up "fallback" = the ALTERNATE archive URL shape on the SAME channel (the pull is
                    // channel-locked; channel fallbacks would fetch a different channel's archive). When the
                    // failover ladder asks, switch shapes once and resume from the current chunk.
                    fallbacks = new();
                    var shapesRef = catchupShapes!; var chId = ch.Id; var chStream = ch.StreamId;
                    next = _ =>
                    {
                        if (catchupShapeIdx + 1 < shapesRef.Count)
                        {
                            catchupShapeIdx++;
                            return Task.FromResult<(int, int, string)?>((chId, chStream, catchupUrlFor(catchupChunkIdx)));
                        }
                        return Task.FromResult<(int, int, string)?>(null);
                    };
                }
                else
                {
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
                    var fbList = fallbacks;
                    next = _ =>
                        fbIndex < fbList.Count
                            ? Task.FromResult<(int, int, string)?>(fbList[fbIndex++])
                            : Task.FromResult<(int, int, string)?>(null);
                }

                var sup = new RecorderSupervisor(new RecorderSupervisor.Deps(_scopes, _gate, _ffmpeg, _tuner, _bus, _lf));
                var task = Task.Run(() => sup.RunAsync(recordingId, url, segDir, outputPath, windowEnd, stall, nativeRate, contentVerify, contentDeadTimeout, contentVerifyHwaccel, contentVerifyFps, cleanEof, bitrateFloor, src?.UserAgent, lease, next, cts.Token, finiteInput, nextChunk, chunkDur), CancellationToken.None);
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

        try
        {
            using var rescueScope = _scopes.CreateScope();
            await DVarr.Services.Events.RescueService.TryOpenTicketAsync(
                rescueScope.ServiceProvider.GetRequiredService<DVarrDbContext>(), _gate,
                rescueScope.ServiceProvider.GetRequiredService<DVarr.Services.SettingsService>(),
                recordingId, "missed: " + why, _log);
        }
        catch (Exception ex) { _log.LogDebug(ex, "[Recorder] rescue-ticket open failed for {Id}", recordingId); }
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
        => !string.IsNullOrWhiteSpace(ch.DirectUrl) ? ch.DirectUrl! : xtream.StreamUrl(src, ch.StreamId);

    private string BuildOutputPath(RecordingEntity rec, Channel ch)
    {
        var title = !string.IsNullOrWhiteSpace(rec.Title) ? rec.Title! : $"Recording {rec.Id}";
        var stamp = EpochTime.ToDisplay(rec.StartUtc).ToString("yyyy-MM-dd_HHmm");
        // The immutable recording id is part of the name (audit REC-02): two recordings with the same title and
        // start minute (duplicate manual schedules, equivalent feeds on two credentials) previously resolved to the
        // SAME flat path and their finalizers overwrote each other with -y. The media import renames on filing, so
        // the id never reaches the library name.
        var name = $"{Sanitize(title)} [{stamp}] [#{rec.Id}].mkv";
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
        // hide credentials embedded in an Xtream /live|/timeshift path URL or a timeshift.php query when logging
        try
        {
            var u = new Uri(url);
            var segs = u.AbsolutePath.Split('/');
            if (segs.Length >= 4 && (segs[1] == "live" || segs[1] == "timeshift")) { segs[2] = "***"; segs[3] = "***"; }
            // Only the timeshift.php query is kept (credentials masked) — it's ours and its params aid debugging.
            // Any OTHER query (a DirectUrl's ?token=… / ?wmsAuthSign=…) is dropped outright, as the old Mask did:
            // masking only username/password would log unknown credential-bearing params verbatim.
            var query = "";
            if (u.AbsolutePath.Contains("timeshift.php", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(u.Query))
                query = System.Text.RegularExpressions.Regex.Replace(u.Query, @"(username|password)=[^&]*", "$1=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{string.Join('/', segs)}{query}";
        }
        catch { return url; }
    }
}
