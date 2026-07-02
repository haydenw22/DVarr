using DVarr.Data;
using DVarr.Services.Ingest;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

/// <summary>
/// Optional daily EPG refresh. When <c>epg_auto_sync_enabled</c> is on, syncs every enabled source's XMLTV guide once
/// per day at <c>epg_auto_sync_time</c> (HH:MM) interpreted in a FIXED UTC offset (<c>epg_auto_sync_offset_minutes</c>).
/// The container ships no tz database (InvariantGlobalization), so the fire time is pure <see cref="DateTimeOffset"/>
/// offset math — no <see cref="TimeZoneInfo"/> lookup, no daylight-saving shift (exact for no-DST zones like Brisbane).
/// The fired-today guard is PERSISTED (epg_auto_sync_last) so a container redeploy after the fire time doesn't re-run a
/// multi-hundred-thousand-programme sync on every deploy. Reuses <see cref="EpgIngestService"/> (last-known-good: a
/// failed source keeps its previous guide) with a fresh DI scope per source. Mirrors <see cref="AutoScheduleService"/>.
/// </summary>
public sealed class EpgAutoSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EpgAutoSyncService> _log;
    private string? _lastFiredLocalDate; // fast-path mirror of the persisted epg_auto_sync_last ("yyyy-MM-dd", local)

    public EpgAutoSyncService(IServiceScopeFactory scopes, ILogger<EpgAutoSyncService> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); } catch (OperationCanceledException) { return; }
        _log.LogInformation("[EpgAuto] Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "[EpgAuto] Tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<int> sourceIds;
        DateTimeOffset localNow;
        using (var scope = _scopes.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            if (!await settings.GetBoolAsync("epg_auto_sync_enabled")) return;

            var offsetMin = await settings.GetIntAsync("epg_auto_sync_offset_minutes");
            if (offsetMin == 0 && (await settings.GetAsync("epg_auto_sync_offset_minutes"))?.Trim() is { Length: > 0 } raw && raw != "0")
            {
                // GetIntAsync returns 0 for a corrupted/non-numeric value — that would silently mean UTC. Say so and
                // fall back to the Brisbane default instead.
                _log.LogWarning("[EpgAuto] epg_auto_sync_offset_minutes is not a number ('{Raw}'); using Brisbane +10", raw);
                offsetMin = 600;
            }
            if (offsetMin < -720 || offsetMin > 840) { _log.LogWarning("[EpgAuto] offset {Min}min out of range; using Brisbane +10", offsetMin); offsetMin = 600; }
            if (!TryParseHm(await settings.GetAsync("epg_auto_sync_time"), out var hh, out var mm)) { hh = 4; mm = 0; }

            // Pure offset math (works under InvariantGlobalization): "now" and today's fire instant in the chosen offset.
            var off = TimeSpan.FromMinutes(offsetMin);
            localNow = DateTimeOffset.UtcNow.ToOffset(off);
            var fire = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, hh, mm, 0, off);
            if (localNow < fire) return; // today's fire time hasn't arrived yet

            // Fired-today guard, persisted across restarts (a redeploy after the fire time must NOT re-run the whole
            // multi-minute sync). In-memory fast-path avoids the settings read churn within a process lifetime.
            var today = localNow.ToString("yyyy-MM-dd");
            if (_lastFiredLocalDate == today) return;
            var persisted = await settings.GetAsync("epg_auto_sync_last");
            if (persisted == today) { _lastFiredLocalDate = today; return; }

            _lastFiredLocalDate = today;
            await settings.SetAsync("epg_auto_sync_last", today, ct); // written BEFORE the sync so a mid-sync restart can't double-fire

            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            sourceIds = await db.Sources.Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct);
        }

        _log.LogInformation("[EpgAuto] daily EPG sync firing for {N} source(s) at local {Time}", sourceIds.Count, localNow.ToString("yyyy-MM-dd HH:mm"));
        foreach (var id in sourceIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Fresh scope per source: a guide sync streams for minutes, so don't hold one DbContext across the lot.
                using var scope = _scopes.CreateScope();
                var epg = scope.ServiceProvider.GetRequiredService<EpgIngestService>();
                var r = await epg.SyncSourceEpgAsync(id, ct);
                if (r.Ok) _log.LogInformation("[EpgAuto] source {Id}: ok ({Prog} programmes)", id, r.Programmes);
                else _log.LogWarning("[EpgAuto] source {Id}: failed — {Error} (its previous guide is kept; next attempt tomorrow)", id, r.Error);
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
