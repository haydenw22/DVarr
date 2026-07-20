using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Recording;
using Microsoft.EntityFrameworkCore;
using RecordingEntity = DVarr.Data.Entities.Recording;

namespace DVarr.Services.Scheduling;

/// <summary>
/// The durable scheduler (docs/05 §3). The recordings table — not an in-memory timer — is the
/// source of truth. On boot it resumes open windows and flags missed ones; each tick it arms
/// recordings whose pre-roll has arrived and records a <see cref="ScheduleTick"/> audit row.
/// Because each credential has one slot, an overlapping recording on a busy credential simply
/// stays PENDING (conflict) until the slot frees or its window passes.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RecorderService _recorder;
    private readonly DbWriteGate _gate;
    private readonly ILogger<SchedulerService> _log;

    // Last "not started" reason logged per recording, so the every-tick arm retry doesn't spam the feed once the
    // message is visible at Information (see the arm loop below).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Err, long At)> _lastStartFail = new();

    public SchedulerService(IServiceScopeFactory scopes, RecorderService recorder, DbWriteGate gate, ILogger<SchedulerService> log)
    {
        _scopes = scopes; _recorder = recorder; _gate = gate; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { return; }

        try { await _recorder.ResumeOrRecoverAsync(stoppingToken); }
        catch (Exception ex) { _log.LogError(ex, "[Scheduler] Boot recovery failed"); }

        _log.LogInformation("[Scheduler] Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 10;
            try { interval = await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[Scheduler] Tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(interval, 5, 30)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int> TickAsync(CancellationToken ct)
    {
        var swStart = EpochTime.Now();
        var now = swStart;
        int examined, started = 0, conflicts = 0, missed = 0, intervalS;

        List<RecordingEntity> due;
        List<int> passedPending;
        List<RecordingEntity> stuck = new();
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            intervalS = await settings.GetIntAsync("tick_interval_s");
            if (intervalS <= 0) intervalS = 10;

            examined = await db.Recordings.CountAsync(r => r.State == RecordingState.Pending, ct);

            due = await db.Recordings
                .Where(r => r.State == RecordingState.Pending
                            && r.StartUtc - r.PrePadS <= now
                            && r.EndUtc + r.PostPadS > now)
                .OrderBy(r => r.StartUtc)
                .ToListAsync(ct);

            passedPending = await db.Recordings
                .Where(r => r.State == RecordingState.Pending && r.EndUtc + r.PostPadS <= now)
                .Select(r => r.Id)
                .ToListAsync(ct);

            // Retry-at-event-start: an active recording whose pre-roll attempt has captured NOTHING by ~30s past the
            // real start (the channel likely wasn't live yet). Make ONE guaranteed fresh attempt; one-shot via
            // EventStartRetried, and the 30s grace means a recording that's simply mid-connect is never restarted.
            if (await settings.GetBoolAsync("retry_at_event_start"))
                stuck = await db.Recordings
                    .Where(r => !r.EventStartRetried && r.BytesWritten == 0
                                && r.StartUtc <= now - 30 && r.EndUtc + r.PostPadS > now
                                && (r.State == RecordingState.Recording || r.State == RecordingState.Recovering
                                    || r.State == RecordingState.FailingOver || r.State == RecordingState.Degraded))
                    .ToListAsync(ct);
        }

        // Re-attempt stuck recordings (started but captured nothing by event start) before the normal arming pass.
        foreach (var r in stuck)
        {
            try
            {
                if (_recorder.IsActive(r.Id)) await _recorder.StopAsync(r.Id); // settle the empty attempt so it can re-arm clean
                await _gate.WriteAsync(async () =>
                {
                    using var rs = _scopes.CreateScope();
                    var rdb = rs.ServiceProvider.GetRequiredService<DVarrDbContext>();
                    var rec = await rdb.Recordings.FindAsync(r.Id);
                    if (rec is null || rec.EventStartRetried) return;
                    rec.State = RecordingState.Pending; rec.EventStartRetried = true;
                    rec.OutputPath = null; rec.SegmentDir = null; rec.FfmpegPid = null; rec.BytesWritten = 0; rec.FailureReason = null;
                    rec.UpdatedUtc = EpochTime.Now();
                    rdb.Notifications.Add(new Notification { RecordingId = r.Id, TsUtc = EpochTime.Now(), Kind = NotificationKind.StalledRelaunched, Severity = Severity.Warn, ToState = "Pending", Message = "no content captured during pre-roll — re-attempting at event start" });
                    await rdb.SaveChangesAsync(ct);
                }, ct);
                var rerr = await _recorder.TryStartAsync(r.Id, ct);
                if (rerr is null) started++;
                _log.LogInformation("[Scheduler] Recording {Id}: nothing captured by event start → re-attempted ({Res})", r.Id, rerr ?? "started");
            }
            catch (Exception ex) { _log.LogWarning(ex, "[Scheduler] event-start retry failed for {Id}", r.Id); }
        }

        foreach (var r in due)
        {
            if (_recorder.IsActive(r.Id)) continue;
            try
            {
                var err = await _recorder.TryStartAsync(r.Id, ct);
                if (err is null) started++;
                else
                {
                    conflicts++;
                    // Information, not Debug: "couldn't arm" is the other invisible recording-loss path (the in-app log
                    // viewer keeps Information and above). The arm is retried EVERY tick, so throttle it — log when the
                    // reason changes, else at most once per 10 minutes per recording.
                    var nowS = EpochTime.Now();
                    var prev = _lastStartFail.TryGetValue(r.Id, out var pv) ? pv : default;
                    if (prev.Err != err || nowS - prev.At > 600)
                    {
                        _lastStartFail[r.Id] = (err, nowS);
                        _log.LogInformation("[Scheduler] Recording {Id} not started: {Err}", r.Id, err);
                    }
                }
            }
            catch (Exception ex)
            {
                // One recording's failure must never abort the whole tick (other due recordings must still arm).
                conflicts++;
                _log.LogError(ex, "[Scheduler] Arming recording {Id} threw", r.Id);
            }
        }

        foreach (var id in passedPending)
        {
            await _recorder.MarkMissedAsync(id, "no free credential slot for the whole window (conflict) or never armed");
            missed++;
        }

        await _gate.WriteAsync(async () =>
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            db.ScheduleTicks.Add(new ScheduleTick
            {
                TickUtc = now,
                RecordingsExamined = examined,
                Started = started,
                Resumed = 0,
                Finalized = 0,
                Missed = missed,
                Conflicts = conflicts,
                DurationMs = (int)((EpochTime.Now() - swStart) * 1000),
            });
            await db.SaveChangesAsync(ct);
        }, ct);

        return intervalS;
    }
}
