using DVarr.Api;
using DVarr.Data;
using DVarr.Infrastructure;
using DVarr.Services;
using DVarr.Services.Events;
using DVarr.Services.Ingest;
using DVarr.Services.Recording;
using DVarr.Services.Scheduling;
using DVarr.Services.Tuner;
using Microsoft.EntityFrameworkCore;

// Pin the content root to the app's own directory so wwwroot resolves whether launched
// via `dotnet run`, the built DLL from any working directory, or inside the container.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// ---------------------------------------------------------------------------
// Path resolution: container volumes (/config,/media,/segments) or local scratch on Windows.
// ---------------------------------------------------------------------------
string ResolveDir(string key, string linuxDefault)
{
    // One precedence chain on every platform: explicit config/env first, then the OS default (audit WIN-01 — the
    // Windows branch used to ignore a configured path entirely, so DVarr__MediaDir etc. silently did nothing there).
    var configured = builder.Configuration[$"DVarr:{key}"];
    if (!string.IsNullOrWhiteSpace(configured)) return configured!;
    return OperatingSystem.IsWindows()
        ? Path.Combine(AppContext.BaseDirectory, "_localdata", key.Replace("Dir", "").ToLowerInvariant())
        : linuxDefault;
}

var configDir = ResolveDir("ConfigDir", "/config");
var mediaDir = ResolveDir("MediaDir", "/media");
var segmentDir = ResolveDir("SegmentDir", "/segments");
foreach (var d in new[] { configDir, mediaDir, segmentDir }) Directory.CreateDirectory(d);

var dbPath = Path.Combine(configDir, "dvarr.db");

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
// In-memory ring buffer of recent log lines backing the in-app Logs viewer (/api/logs). One instance both feeds the
// logger provider and is resolved by the endpoint. Purely in-memory (nothing to disk; cleared on restart).
var logBuffer = new LogRingBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new RingBufferLoggerProvider(logBuffer));
// Framework HttpClient categories log every request URL at Information — for provider calls that's the IPTV login
// verbatim (audit LOG-01). Silence them below Warning in EVERY sink (console + ring); app code logs what matters.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddSingleton(new RuntimePaths(configDir, mediaDir, segmentDir));
builder.Services.AddSingleton<DbWriteGate>();
builder.Services.AddSingleton<SqlitePragmaInterceptor>();
builder.Services.AddSingleton<FfmpegLocator>();
builder.Services.AddSingleton<RecordingEventBus>();
builder.Services.AddSingleton<TunerLeaseManager>();
builder.Services.AddSingleton<RecorderService>();
builder.Services.AddSingleton<DVarr.Services.Recording.PreviewTranscodeManager>();
builder.Services.AddSingleton<DVarr.Services.Media.LibraryPlaybackManager>();
builder.Services.AddSingleton<DVarr.Services.Recording.RecordingPreviewManager>();
builder.Services.AddHostedService<DVarr.Services.Recording.PreviewSweeper>();
// The library reconciler is both injectable (on-demand scans from the API) and hosted (startup backfill + interval).
builder.Services.AddSingleton<DVarr.Services.Media.LibraryScanService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DVarr.Services.Media.LibraryScanService>());

builder.Services.AddDbContext<DVarrDbContext>((sp, opt) =>
    opt.UseSqlite($"Data Source={dbPath}")
       .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()));

builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<SourceSeeder>();
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<EpgIngestService>();
builder.Services.AddScoped<EventIngestService>();
builder.Services.AddScoped<ResolverService>();
builder.Services.AddScoped<EpgRepickService>();
builder.Services.AddScoped<CreditAwarePlanner>();
builder.Services.AddScoped<DVarr.Services.Media.MediaImportService>();
builder.Services.AddScoped<DVarr.Services.Media.LibraryService>();
builder.Services.AddScoped<DVarr.Services.Media.RetentionService>();
builder.Services.AddHttpClient<XtreamClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
builder.Services.AddHttpClient<EventFetcher>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
builder.Services.AddHttpClient<TheSportsDbClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
// Dedicated client for live-preview upstream: no timeout (a preview is an open-ended live stream).
builder.Services.AddHttpClient("preview", c => c.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.AddHttpClient();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHostedService<AutoScheduleService>();
builder.Services.AddHostedService<AutoStopService>(); // smart auto-stop: extends live recordings while the guide says the event is still in play
builder.Services.AddHostedService<HaWebhookService>();
builder.Services.AddHostedService<EpgAutoSyncService>();
builder.Services.AddHostedService<DVarr.Services.Events.RescueSweepService>(); // second-chance replay hunting: re-air rescue for failed games

var urls = builder.Configuration["DVarr:Urls"] ?? "http://0.0.0.0:1867";
builder.WebHost.UseUrls(urls);

var app = builder.Build();

app.Logger.LogInformation("DVarr starting. config={Config} media={Media} segments={Segments}", configDir, mediaDir, segmentDir);

// ---------------------------------------------------------------------------
// Startup: migrate schema, seed settings + sources (no provider calls).
// ---------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();

    // Pre-migration safety copy (audit MIG-01): before applying PENDING migrations to an existing database, snapshot
    // the SQLite file (+ WAL/SHM sidecars) so a failed/interrupted migration is recoverable by copying the backup
    // back. Keeps the last 3 snapshots. A fresh database (no file yet) or an up-to-date one skips this entirely.
    try
    {
        if (File.Exists(dbPath) && (await db.Database.GetPendingMigrationsAsync()).Any())
        {
            var backupDir = Path.Combine(configDir, "backups");
            Directory.CreateDirectory(backupDir);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = dbPath + suffix;
                if (File.Exists(src)) File.Copy(src, Path.Combine(backupDir, $"pre-migrate-{stamp}.db{suffix}"), overwrite: true);
            }
            foreach (var old in Directory.GetFiles(backupDir, "pre-migrate-*")
                         .Where(f => f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) // exact .db (the 3-char pattern quirk would sweep -wal names into the ordering)
                         .OrderByDescending(f => f, StringComparer.Ordinal).Skip(3))
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    { var p = old + suffix; if (File.Exists(p)) File.Delete(p); }
            app.Logger.LogInformation("Pre-migration database backup written to {Dir} (pre-migrate-{Stamp}.db)", backupDir, stamp);
        }
    }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Pre-migration backup failed — continuing with migration"); }

    await db.Database.MigrateAsync();

    var settingsSvc = scope.ServiceProvider.GetRequiredService<SettingsService>();
    await settingsSvc.EnsureDefaultsAsync();
    // Point display-time conversion (UI clock, filenames, Plex air dates) at the configured zone before anything renders.
    EpochTime.SetDisplayZone(await settingsSvc.GetAsync("timezone_display"));
    app.Logger.LogInformation("Display timezone: {Zone}", EpochTime.DisplayZone.Id);
    await scope.ServiceProvider.GetRequiredService<SourceSeeder>().SeedFromFileAsync(configDir);

    var gate = scope.ServiceProvider.GetRequiredService<DbWriteGate>();
    // Never echo the key itself into any log (audit LOG-01 — the in-app Logs page would display it); the
    // logged-in owner reads it from /api/parity/apikey instead.
    var (_, apiKeyCreated) = await DVarr.Api.ParityEndpoints.EnsureApiKeyAsync(db, gate);
    app.Logger.LogInformation(apiKeyCreated
        ? "Sonarr-emulation API key generated — view it (logged in) at /api/parity/apikey and paste into Prowlarr."
        : "Sonarr-emulation API key already configured.");

    // Per-install secret for the Plex/Jellyfin watched webhooks (URL token; shown under Settings → Storage).
    var (_, webhookTokenCreated) = await DVarr.Api.WebhookEndpoints.EnsureWebhookTokenAsync(db, gate);
    if (webhookTokenCreated) app.Logger.LogInformation("Media-server webhook token generated (URLs under Settings → Storage).");

    var (_, calTokenCreated) = await DVarr.Api.CalendarEndpoints.EnsureCalendarTokenAsync(db, gate);
    // Only note the token was created (the copy-me URL is available at /api/calendar/url); don't re-log it every boot.
    if (calTokenCreated) app.Logger.LogInformation("Calendar feed token generated (subscribe URL at /api/calendar/url).");

    // Session-cookie signing key (32 random bytes, persisted in Secrets). Created on first boot; rotating that row
    // logs every trusted device out. Never logged — it's an HMAC key, not a user-facing token.
    await DVarr.Api.AuthEndpoints.EnsureSessionSigningKeyAsync(db, gate);

    var ff = scope.ServiceProvider.GetRequiredService<FfmpegLocator>();
    ff.CachedVersion = await ff.VersionAsync();
    app.Logger.LogInformation("ffmpeg check: {Ver}", ff.CachedVersion ?? "NOT FOUND (recording will fail until ffmpeg is on PATH)");
}

// Gate the WHOLE site (SPA shell + static assets + /api/*) behind HTTP Basic auth — must precede
// UseDefaultFiles/UseStaticFiles so index.html and assets are protected too. Exempt list (m2m surfaces)
// lives in the middleware. Log the credential MODE only — never the values.
{
    var authUser = builder.Configuration["DVARR_AUTH_USER"] ?? builder.Configuration["DVarr:AuthUser"];
    var authPass = builder.Configuration["DVARR_AUTH_PASS"] ?? builder.Configuration["DVarr:AuthPass"];
    if (authUser is null && authPass is null)
        app.Logger.LogWarning("Basic auth: DEFAULT credentials (user/password) — set DVARR_AUTH_USER/DVARR_AUTH_PASS in .env!");
    else
        app.Logger.LogInformation("Basic auth: custom credentials configured");
}
app.UseMiddleware<DVarr.Infrastructure.BasicAuthMiddleware>();

app.UseDefaultFiles();
// .webmanifest isn't in the default extension→MIME map, so without this the PWA manifest would be skipped (404 →
// SPA fallback) instead of served. Register it (other shell assets — .js/.css/.png — are already known types).
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.MapHealthEndpoints();
app.MapAuthApi();
app.MapDVarrApi();
app.MapLeagueApi();
app.MapParityApi();
app.MapCalendarApi();
app.MapPreviewApi();
app.MapLibraryApi();
app.MapWebhookApi();
app.MapPlexApi();
app.MapFallbackToFile("index.html");

app.Run();
