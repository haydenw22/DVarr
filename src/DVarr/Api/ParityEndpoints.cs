using System.Text;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Ingest;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

/// <summary>
/// P3 parity: a stream proxy (so exported M3Us carry no provider credentials), filtered M3U/EPG export,
/// Home Assistant status, and a minimal Sonarr-v3 surface for Prowlarr.
/// </summary>
public static class ParityEndpoints
{
    private static readonly RecordingState[] Active =
    {
        RecordingState.Starting, RecordingState.Recording, RecordingState.Recovering,
        RecordingState.FailingOver, RecordingState.Degraded, RecordingState.Stopping, RecordingState.Finalizing,
    };

    public static void MapParityApi(this WebApplication app)
    {
        // Credential-free stream proxy: the M3U points here; we 302 to the real provider .ts at play time,
        // so the exported playlist file never contains the login (docs/05 §6).
        app.MapGet("/api/stream/{channelId:int}.ts", async (int channelId, DVarrDbContext db, XtreamClient xtream) =>
        {
            var ch = await db.Channels.FindAsync(channelId);
            if (ch is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(ch.DirectUrl)) return Results.Redirect(ch.DirectUrl!);
            var src = await db.Sources.FindAsync(ch.SourceId);
            if (src is null) return Results.NotFound();
            if (!src.Enabled) return Results.NotFound(); // off-limits: don't expose a disabled source's stream URL
            return Results.Redirect(xtream.StreamTsUrl(src, ch.StreamId));
        });

        // Filtered M3U = the channels you've mapped to leagues (the ones you care about), via the proxy.
        app.MapGet("/api/iptv/filtered.m3u", async (HttpContext ctx, DVarrDbContext db) =>
        {
            var chIds = await db.LeagueChannelMaps.Select(m => m.ChannelId).Distinct().ToListAsync();
            var chans = await db.Channels.Where(c => chIds.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync();
            var host = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var sb = new StringBuilder("#EXTM3U\n");
            foreach (var c in chans)
                sb.Append($"#EXTINF:-1 tvg-id=\"{M3u(c.EpgChannelId)}\" tvg-name=\"{M3u(c.Name)}\" group-title=\"{M3u(c.GroupName ?? "")}\",{M3u(c.Name)}\n{host}/api/stream/{c.Id}.ts\n");
            return Results.Text(sb.ToString(), "audio/x-mpegurl");
        });

        // Filtered XMLTV for the mapped channels.
        app.MapGet("/api/iptv/filtered.xml", async (DVarrDbContext db) =>
        {
            var chIds = await db.LeagueChannelMaps.Select(m => m.ChannelId).Distinct().ToListAsync();
            var chans = await db.Channels.Where(c => chIds.Contains(c.Id)).ToListAsync();
            // EPG is now keyed per (source, tvg-id); map those back to our channel ids for the export.
            // Effective tvg-id = provider's epg_channel_id, else the name-matched one.
            static string? Eff(Channel c) => !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : (string.IsNullOrEmpty(c.MatchedEpgId) ? null : c.MatchedEpgId);
            var withEpg = chans.Where(c => Eff(c) != null).ToList();
            var srcIds = withEpg.Select(c => c.SourceId).Distinct().ToList();
            var epgIds = withEpg.Select(c => Eff(c)!).Distinct().ToList();
            // Programme.EpgChannelId is COLLATE NOCASE → the IN below is case-insensitive + index-seekable; join in memory by lowercased key.
            var byKey = withEpg.GroupBy(c => (c.SourceId, Eff(c)!.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());
            var progs = (srcIds.Count == 0) ? new() : await db.Programmes
                .Where(p => srcIds.Contains(p.SourceId) && epgIds.Contains(p.EpgChannelId))
                .OrderBy(p => p.StartUtc).Take(20000).ToListAsync();
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv generator-info-name=\"DVarr\">\n");
            foreach (var c in chans)
                sb.Append($"  <channel id=\"dvarr-{c.Id}\"><display-name>{Esc(c.Name)}</display-name></channel>\n");
            foreach (var p in progs)
                if (byKey.TryGetValue((p.SourceId, p.EpgChannelId.ToLowerInvariant()), out var cids))
                    foreach (var cid in cids)
                        sb.Append($"  <programme start=\"{Xmltv(p.StartUtc)}\" stop=\"{Xmltv(p.StopUtc)}\" channel=\"dvarr-{cid}\"><title>{Esc(p.Title)}</title></programme>\n");
            sb.Append("</tv>\n");
            return Results.Text(sb.ToString(), "application/xml");
        });

        // ---- Home Assistant status (poll this from a REST sensor) ----
        app.MapGet("/api/ha/status", async (DVarrDbContext db) =>
        {
            var now = EpochTime.Now();
            var active = await db.Recordings.Where(r => Active.Contains(r.State))
                .Select(r => new { r.Id, r.Title, state = r.State.ToString() }).ToListAsync();
            var nextStart = await db.Recordings.Where(r => r.State == RecordingState.Pending && r.StartUtc > now)
                .OrderBy(r => r.StartUtc).Select(r => (long?)r.StartUtc).FirstOrDefaultAsync();
            // Only enabled sources can lease a tuner, so free_credentials must count enabled — not total — sources
            // (otherwise a disabled login inflates the "free" figure HA shows).
            var sources = await db.Sources.CountAsync(s => s.Enabled);
            var busy = await db.TunerLeases.Where(l => l.State == LeaseState.Active).Select(l => l.SourceId).Distinct().CountAsync();
            return Results.Json(new
            {
                recording = active.Count > 0,
                recording_count = active.Count,
                recordings = active,
                pending = await db.Recordings.CountAsync(r => r.State == RecordingState.Pending),
                next_recording_utc = nextStart,
                free_credentials = Math.Max(0, sources - busy),
                ts = now,
            });
        });

        // ---- Minimal Sonarr v3 surface for Prowlarr (X-Api-Key). Partial by design (P3). ----
        var v3 = app.MapGroup("/api/v3");
        v3.MapGet("/system/status", async (HttpContext ctx, DVarrDbContext db) =>
            await Authed(ctx, db) ? Results.Json(new
            {
                appName = "DVarr", instanceName = "DVarr", version = "4.0.0.0",
                buildTime = "2026-06-15T00:00:00Z", isProduction = true,
                authentication = "apikey", appData = "/config", osName = "linux",
            }) : Results.Unauthorized());

        v3.MapGet("/qualityprofile", async (HttpContext ctx, DVarrDbContext db) =>
            await Authed(ctx, db) ? Results.Json(new[] { new { id = 1, name = "Any" } }) : Results.Unauthorized());
        v3.MapGet("/rootfolder", async (HttpContext ctx, DVarrDbContext db) =>
            await Authed(ctx, db) ? Results.Json(new[] { new { id = 1, path = "/media", accessible = true, freeSpace = 0L } }) : Results.Unauthorized());
        v3.MapGet("/tag", async (HttpContext ctx, DVarrDbContext db) => await Authed(ctx, db) ? Results.Json(Array.Empty<object>()) : Results.Unauthorized());
        v3.MapGet("/series", async (HttpContext ctx, DVarrDbContext db) => await Authed(ctx, db) ? Results.Json(Array.Empty<object>()) : Results.Unauthorized());
        v3.MapPost("/command", async (HttpContext ctx, DVarrDbContext db) => await Authed(ctx, db) ? Results.Json(new { id = 1, status = "completed" }) : Results.Unauthorized());
    }

    /// <summary>Returns the Sonarr-emulation API key, generating it on first call. <c>Created</c> is true only on
    /// the boot that generated it, so the caller can avoid echoing the secret into the log on every subsequent boot.</summary>
    public static async Task<(string Key, bool Created)> EnsureApiKeyAsync(DVarrDbContext db, DbWriteGate gate)
    {
        var row = await db.Secrets.FirstOrDefaultAsync(s => s.Name == "sonarr_api_key");
        if (row is not null) return (row.Value, false);
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await gate.WriteAsync(async () =>
        {
            db.Secrets.Add(new Data.Entities.SecretEntry { Name = "sonarr_api_key", Value = key, CreatedUtc = EpochTime.Now(), UpdatedUtc = EpochTime.Now() });
            await db.SaveChangesAsync();
        });
        return (key, true);
    }

    private static async Task<bool> Authed(HttpContext ctx, DVarrDbContext db)
    {
        var provided = ctx.Request.Headers["X-Api-Key"].FirstOrDefault() ?? ctx.Request.Query["apikey"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided)) return false;
        var key = (await db.Secrets.FirstOrDefaultAsync(s => s.Name == "sonarr_api_key"))?.Value;
        if (string.IsNullOrEmpty(key)) return false;
        // Constant-time compare so the key can't be recovered byte-by-byte via response-timing analysis.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(key));
    }

    // XML escape + strip XML-illegal control chars (provider channel/programme names can carry them).
    private static string Esc(string? s)
    {
        var clean = new string((s ?? "").Where(c => c == '\t' || c == '\n' || c == '\r' || c >= ' ').ToArray());
        return clean.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    // M3U is line-oriented: drop CR/LF and other control chars so a crafted channel/EPG id can't inject an
    // extra #EXTINF line or a rogue stream URL, and neutralise the double-quote that delimits attributes.
    private static string M3u(string? s) => new string((s ?? "").Where(c => c >= ' ').ToArray()).Replace("\"", "'");

    private static string Xmltv(long epoch) => EpochTime.ToUtc(epoch).ToString("yyyyMMddHHmmss") + " +0000";
}
