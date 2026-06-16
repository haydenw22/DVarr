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
        }

        foreach (var r in due)
        {
            if (_recorder.IsActive(r.Id)) continue;
            try
            {
                var err = await _recorder.TryStartAsync(r.Id, ct);
                if (err is null) started++;
                else { conflicts++; _log.LogDebug("[Scheduler] Recording {Id} not started: {Err}", r.Id, err); }
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
