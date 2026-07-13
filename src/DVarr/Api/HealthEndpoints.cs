using System.Reflection;
using DVarr.Data;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Prefer the SemVer InformationalVersion (csproj <Version>, e.g. "1.12.0"); strip any +build metadata.
        var asm = typeof(HealthEndpoints).Assembly;
        var version = (asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString() ?? "0.0.0").Split('+')[0];

        var activeStates = new[]
        {
            DVarr.Data.RecordingState.Starting, DVarr.Data.RecordingState.Recording,
            DVarr.Data.RecordingState.Recovering, DVarr.Data.RecordingState.FailingOver,
            DVarr.Data.RecordingState.Degraded, DVarr.Data.RecordingState.Stopping,
            DVarr.Data.RecordingState.Finalizing,
        };

        app.MapGet("/api/health", async (DVarrDbContext db, DVarr.Infrastructure.FfmpegLocator ff) =>
        {
            var now = EpochTime.Now();

            // Readiness = the queries this page actually needs succeed, not just "a connection can open" — and a
            // locked/corrupt database must degrade the payload rather than throw an unhandled 500 out of the one
            // endpoint that's supposed to report exactly that state (audit HEALTH-01).
            bool dbOk;
            string? dbErr = null;
            int sources = 0, busyCredentials = 0, active = 0, pending = 0;
            long? lastTick = null;
            try
            {
                dbOk = await db.Database.CanConnectAsync();
                if (dbOk)
                {
                    // Only ENABLED sources count toward the concurrency ceiling — a disabled source can never hold a
                    // lease, so it must not inflate total/free_credentials.
                    sources = await db.Sources.CountAsync(s => s.Enabled);
                    busyCredentials = await db.TunerLeases
                        .Where(l => l.State == LeaseState.Active)
                        .Select(l => l.SourceId)
                        .Distinct()
                        .CountAsync();
                    active = await db.Recordings.CountAsync(r => activeStates.Contains(r.State));
                    pending = await db.Recordings.CountAsync(r => r.State == DVarr.Data.RecordingState.Pending);
                    lastTick = await db.ScheduleTicks
                        .OrderByDescending(t => t.TickUtc)
                        .Select(t => (long?)t.TickUtc)
                        .FirstOrDefaultAsync();
                }
            }
            catch (Exception ex) { dbOk = false; dbErr = ex.Message; }
            var freeCredentials = Math.Max(0, sources - busyCredentials);

            return Results.Json(new
            {
                status = dbOk ? "ok" : "degraded",
                app = "DVarr",
                version,
                ffmpeg = ff.CachedVersion,
                time = new
                {
                    utc_epoch = now,
                    local = EpochTime.ToDisplay(now).ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    zone = EpochTime.DisplayZone.Id,
                },
                db = new { ok = dbOk, error = dbErr, mode = "sqlite-wal" },
                // Concurrency ceiling == number of credentials (1 stream each).
                sources = new { total = sources, free_credentials = freeCredentials },
                recordings = new { active, pending },
                scheduler = new { last_tick_utc = lastTick },
            });
        });
    }
}
