using DVarr.Data;
using DVarr.Services.Ingest;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

/// <summary>
/// Optional daily EPG refresh. When <c>epg_auto_sync_enabled</c> is on, syncs every enabled source's XMLTV guide once
/// per day at <c>epg_auto_sync_time</c> (HH:MM) interpreted in a FIXED UTC offset (<c>epg_auto_sync_offset_minutes</c>).
/// The container ships no tz database (InvariantGlobalization), so the fire time is pure <see cref="DateTimeOffset"/>
/// offset math — no <see cref="TimeZoneInfo"/> lookup, no daylight-saving shift (exact for no-DST zones like Brisbane).
/// Reuses <see cref="EpgIngestService"/> (last-known-good: a failed source keeps its previous guide). Mirrors
/// <see cref="AutoScheduleService"/>'s BackgroundService loop.
/// </summary>
public sealed class EpgAutoSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EpgAutoSyncService> _log;
    private DateTime? _lastFiredLocalDate; // local calendar date of the last run; null until the first fire

    public EpgAutoSyncService(IServiceScopeFactory scopes, ILogger<EpgAutoSyncService> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); } catch (OperationCanceledException) { return; }
        _log.LogInformation("[EpgAuto] Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[EpgAuto] Tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        if (!await settings.GetBoolAsync("epg_auto_sync_enabled")) return;

        var offsetMin = await settings.GetIntAsync("epg_auto_sync_offset_minutes");
        if (offsetMin < -720 || offsetMin > 840) offsetMin = 600; // valid UTC offsets are -12:00..+14:00; default Brisbane +10
        if (!TryParseHm(await settings.GetAsync("epg_auto_sync_time"), out var hh, out var mm)) { hh = 4; mm = 0; }

        // Pure offset math (works under InvariantGlobalization): "now" and today's fire instant in the chosen offset.
        var off = TimeSpan.FromMinutes(offsetMin);
        var localNow = DateTimeOffset.UtcNow.ToOffset(off);
        var fire = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, hh, mm, 0, off);
        if (localNow < fire) return;                      // today's fire time hasn't arrived yet
        if (_lastFiredLocalDate == localNow.Date) return; // already fired today (in-memory guard; a restart past the
                                                          // fire time triggers one harmless catch-up — sync is idempotent)

        _lastFiredLocalDate = localNow.Date;
        await SyncAllSourcesAsync(scope, localNow, ct);
    }

    private async Task SyncAllSourcesAsync(IServiceScope scope, DateTimeOffset localNow, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
        var epg = scope.ServiceProvider.GetRequiredService<EpgIngestService>();
        var sourceIds = await db.Sources.Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct);
        _log.LogInformation("[EpgAuto] daily EPG sync firing for {N} source(s) at local {Time}", sourceIds.Count, localNow.ToString("yyyy-MM-dd HH:mm"));
        foreach (var id in sourceIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await epg.SyncSourceEpgAsync(id, ct);
                _log.LogInformation("[EpgAuto] source {Id}: {Status} ({Prog} programmes)", id, r.Ok ? "ok" : $"failed: {r.Error}", r.Programmes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "[EpgAuto] source {Id} sync failed", id); }
        }
    }

    private static bool TryParseHm(string? s, out int hh, out int mm)
    {
        hh = mm = 0;
        var parts = s?.Trim().Split(':');
        if (parts is not { Length: >= 2 }) return false;
        return int.TryParse(parts[0], out hh) && int.TryParse(parts[1], out mm) && hh is >= 0 and <= 23 && mm is >= 0 and <= 59;
    }
}
