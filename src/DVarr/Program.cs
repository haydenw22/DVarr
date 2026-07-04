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
    if (OperatingSystem.IsWindows())
        return Path.Combine(AppContext.BaseDirectory, "_localdata", key.Replace("Dir", "").ToLowerInvariant());
    var configured = builder.Configuration[$"DVarr:{key}"];
    return !string.IsNullOrWhiteSpace(configured) ? configured! : linuxDefault;
}

var configDir = ResolveDir("ConfigDir", "/config");
var mediaDir = ResolveDir("MediaDir", "/media");
var segmentDir = ResolveDir("SegmentDir", "/segments");
foreach (var d in new[] { configDir, mediaDir, segmentDir }) Directory.CreateDirectory(d);

var dbPath = Path.Combine(configDir, "dvarr.db");

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(new RuntimePaths(configDir, mediaDir, segmentDir));
builder.Services.AddSingleton<DbWriteGate>();
builder.Services.AddSingleton<SqlitePragmaInterceptor>();
builder.Services.AddSingleton<FfmpegLocator>();
builder.Services.AddSingleton<RecordingEventBus>();
builder.Services.AddSingleton<TunerLeaseManager>();
builder.Services.AddSingleton<RecorderService>();
builder.Services.AddSingleton<DVarr.Services.Recording.PreviewTranscodeManager>();
builder.Services.AddHostedService<DVarr.Services.Recording.PreviewSweeper>();

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
    await db.Database.MigrateAsync();

    await scope.ServiceProvider.GetRequiredService<SettingsService>().EnsureDefaultsAsync();
    await scope.ServiceProvider.GetRequiredService<SourceSeeder>().SeedFromFileAsync(configDir);

    var gate = scope.ServiceProvider.GetRequiredService<DbWriteGate>();
    var (apiKey, apiKeyCreated) = await DVarr.Api.ParityEndpoints.EnsureApiKeyAsync(db, gate);
    // Echo the secret only on the boot that generates it — don't re-print it into the log on every restart.
    if (apiKeyCreated)
        app.Logger.LogInformation("Sonarr-emulation API key generated (paste into Prowlarr): {Key}", apiKey);
    else
        app.Logger.LogInformation("Sonarr-emulation API key already configured.");

    var (_, calTokenCreated) = await DVarr.Api.CalendarEndpoints.EnsureCalendarTokenAsync(db, gate);
    // Only note the token was created (the copy-me URL is available at /api/calendar/url); don't re-log it every boot.
    if (calTokenCreated) app.Logger.LogInformation("Calendar feed token generated (subscribe URL at /api/calendar/url).");

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
app.MapDVarrApi();
app.MapLeagueApi();
app.MapParityApi();
app.MapCalendarApi();
app.MapPreviewApi();
app.MapPlexApi();
app.MapFallbackToFile("index.html");

app.Run();
