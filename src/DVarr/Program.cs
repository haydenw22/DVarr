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
builder.Services.AddHostedService<HaWebhookService>();

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

    var (apiKey, apiKeyCreated) = await DVarr.Api.ParityEndpoints.EnsureApiKeyAsync(db, scope.ServiceProvider.GetRequiredService<DbWriteGate>());
    // Echo the secret only on the boot that generates it — don't re-print it into the log on every restart.
    if (apiKeyCreated)
        app.Logger.LogInformation("Sonarr-emulation API key generated (paste into Prowlarr): {Key}", apiKey);
    else
        app.Logger.LogInformation("Sonarr-emulation API key already configured.");

    var ff = scope.ServiceProvider.GetRequiredService<FfmpegLocator>();
    ff.CachedVersion = await ff.VersionAsync();
    app.Logger.LogInformation("ffmpeg check: {Ver}", ff.CachedVersion ?? "NOT FOUND (recording will fail until ffmpeg is on PATH)");
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthEndpoints();
app.MapDVarrApi();
app.MapLeagueApi();
app.MapParityApi();
app.MapPreviewApi();
app.MapPlexApi();
app.MapFallbackToFile("index.html");

app.Run();
