using System.Text;
using System.Text.Json;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

/// <summary>
/// Inbound "watched" webhooks from Plex / Jellyfin. They flip a library file's <c>WatchedUtc</c> so the
/// "delete after watched" retention mode can act. The two POST endpoints are login-exempt (a media server can't
/// present DVarr's login — see BasicAuthMiddleware) but carry their OWN per-install secret token in the URL
/// (<c>?token=…</c>, generated once into Secrets and shown under Settings → Storage), mirroring the calendar feed
/// (audit SEC-08): without it every request is 401 and nothing is written. Matching is by file path or the
/// reported series/episode, and refuses ambiguity — an unauthenticated or sloppy payload can never feed the auto-delete plan.
/// </summary>
public static class WebhookEndpoints
{
    private const string TokenSecretName = "webhook_token";
    private const long PlexMaxBodyBytes = 10 * 1024 * 1024;   // multipart can carry a poster thumbnail
    private const long JellyfinMaxBodyBytes = 1024 * 1024;    // plain JSON — 1 MB is already generous

    public static void MapWebhookApi(this WebApplication app)
    {
        // Plex: multipart/form-data with a `payload` JSON field. media.scrobble fires at ~90% watched.
        app.MapPost("/api/webhooks/plex", async (HttpRequest req, DVarrDbContext db, DbWriteGate gate, ILoggerFactory lf) =>
        {
            if (!await TokenValidAsync(req, db)) return Results.Unauthorized();
            if (req.ContentLength is > PlexMaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var log = lf.CreateLogger("DVarr.Webhook");
            try
            {
                string? json;
                if (req.HasFormContentType) { var form = await req.ReadFormAsync(); json = form["payload"]; }
                else { using var r = new StreamReader(req.Body); json = await r.ReadToEndAsync(); }
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json!);
                    var root = doc.RootElement;
                    if (FirstString(root, "event") == "media.scrobble")
                    {
                        var meta = root.TryGetProperty("Metadata", out var m) ? m : default;
                        var path = FirstString(meta, "file") ?? DeepFilePath(meta);
                        var title = FirstString(meta, "title");
                        var series = FirstString(meta, "grandparentTitle");
                        var season = IntField(meta, "parentIndex");
                        var episode = IntField(meta, "index");
                        var n = await MarkWatchedAsync(db, gate, path, series, season, episode, log);
                        log.LogInformation("[Webhook] plex scrobble '{Title}' (series={Series} S{Season}E{Episode}, path={Path}) → {N} item(s) marked watched",
                            title ?? "?", series ?? "?", season, episode, path ?? "(none)", n);
                    }
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "[Webhook] plex parse failed"); }
            return Results.Ok();
        });

        // Jellyfin (Webhook plugin): JSON body. Treat any payload flagged played/played-to-completion as watched
        // (the plugin's templates vary: PlaybackStop + PlayedToCompletion, or a UserDataSaved with Played).
        app.MapPost("/api/webhooks/jellyfin", async (HttpRequest req, DVarrDbContext db, DbWriteGate gate, ILoggerFactory lf) =>
        {
            if (!await TokenValidAsync(req, db)) return Results.Unauthorized();
            if (req.ContentLength is > JellyfinMaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var log = lf.CreateLogger("DVarr.Webhook");
            try
            {
                using var r = new StreamReader(req.Body);
                var json = await r.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (BoolField(root, "PlayedToCompletion") || BoolField(root, "Played"))
                    {
                        var path = FirstString(root, "ItemPath") ?? FirstString(root, "Path")
                                   ?? (root.TryGetProperty("Item", out var it) ? FirstString(it, "Path") : null);
                        var title = FirstString(root, "Name") ?? FirstString(root, "ItemName");
                        var series = FirstString(root, "SeriesName");
                        var season = IntField(root, "SeasonNumber");
                        var episode = IntField(root, "EpisodeNumber");
                        var n = await MarkWatchedAsync(db, gate, path, series, season, episode, log);
                        log.LogInformation("[Webhook] jellyfin '{Title}' (series={Series} S{Season}E{Episode}, path={Path}) → {N} item(s) marked watched",
                            title ?? "?", series ?? "?", season, episode, path ?? "(none)", n);
                    }
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "[Webhook] jellyfin parse failed"); }
            return Results.Ok();
        });

        // The copy-me URLs (token included) for Settings → Storage. Behind the app's normal auth — only the exact
        // POST paths above are login-exempt.
        app.MapGet("/api/webhooks/urls", async (DVarrDbContext db, DbWriteGate gate) =>
        {
            var (token, _) = await EnsureWebhookTokenAsync(db, gate);
            return Results.Json(new
            {
                plex = $"/api/webhooks/plex?token={token}",
                jellyfin = $"/api/webhooks/jellyfin?token={token}",
            });
        });
    }

    /// <summary>Returns the webhook token, generating a 32-hex secret on first call. <c>Created</c> is true only on
    /// the boot that generated it. Mirrors <see cref="CalendarEndpoints.EnsureCalendarTokenAsync"/> (Secrets storage).</summary>
    public static async Task<(string Token, bool Created)> EnsureWebhookTokenAsync(DVarrDbContext db, DbWriteGate gate)
    {
        var row = await db.Secrets.FirstOrDefaultAsync(s => s.Name == TokenSecretName);
        if (row is not null) return (row.Value, false);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await gate.WriteAsync(async () =>
        {
            db.Secrets.Add(new SecretEntry { Name = TokenSecretName, Value = token, CreatedUtc = EpochTime.Now(), UpdatedUtc = EpochTime.Now() });
            await db.SaveChangesAsync();
        });
        return (token, true);
    }

    private static async Task<bool> TokenValidAsync(HttpRequest req, DVarrDbContext db)
    {
        var provided = req.Query["token"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided)) return false;
        var token = (await db.Secrets.FirstOrDefaultAsync(s => s.Name == TokenSecretName))?.Value;
        if (string.IsNullOrEmpty(token)) return false;
        // Constant-time compare so the token can't be recovered byte-by-byte via response-timing analysis.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Mark the matching library item watched. Tries the on-disk PATH first (exact, then a unique filename —
    /// a media server's mount path often differs from DVarr's), then falls back to the SERIES + SEASON + EPISODE the
    /// server reports (SeriesName/SeasonNumber/EpisodeNumber ⇄ DVarr's ShowName/SeasonYear/EpisodeNum, its
    /// League/Season/Game filing) — Jellyfin's "watched" webhook frequently carries no file path at all, so the
    /// structured key is what actually lands. Every path refuses ambiguity: a key resolving to several rows is skipped,
    /// not guessed at (audit SEC-08), because watched state can feed the auto-delete plan. Returns rows flagged.</summary>
    private static async Task<int> MarkWatchedAsync(DVarrDbContext db, DbWriteGate gate, string? path,
        string? series, int? season, int? episode, ILogger log)
    {
        var ids = new List<int>();

        // 1) By on-disk path: exact, then a UNIQUE filename match.
        if (!string.IsNullOrWhiteSpace(path))
        {
            ids = await db.LibraryItems.Where(i => i.FilePath == path).Select(i => i.Id).ToListAsync();
            if (ids.Count == 0)
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var byName = await db.LibraryItems.Where(i => i.FilePath.EndsWith(name)).Select(i => i.Id).ToListAsync();
                    if (byName.Count > 1)
                    {
                        log.LogWarning("[Webhook] filename '{Name}' matches {N} library files — ambiguous, not marking watched", name, byName.Count);
                        return 0;
                    }
                    ids = byName;
                }
            }
        }

        // 2) By series + season + episode (the reliable fallback when no path is sent). All three required; unique only.
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(series) && season is > 0 && episode is > 0)
        {
            var s = series.Trim();
            ids = await db.LibraryItems
                .Where(i => i.SeasonYear == season!.Value && i.EpisodeNum == episode!.Value && i.ShowName == s)
                .Select(i => i.Id).ToListAsync();
            if (ids.Count == 0) // the media server's series casing can differ from DVarr's folder name
                ids = await db.LibraryItems
                    .Where(i => i.SeasonYear == season!.Value && i.EpisodeNum == episode!.Value && i.ShowName.ToLower() == s.ToLower())
                    .Select(i => i.Id).ToListAsync();
            if (ids.Count > 1)
            {
                log.LogWarning("[Webhook] '{Series}' S{Season}E{Episode} matches {N} library files — ambiguous, not marking watched", s, season, episode, ids.Count);
                return 0;
            }
        }

        if (ids.Count == 0) return 0;
        var now = EpochTime.Now();
        await gate.WriteAsync(async () =>
            await db.LibraryItems.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(x => x.SetProperty(i => i.WatchedUtc, now)));
        return ids.Count;
    }

    private static int? IntField(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static string? FirstString(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool BoolField(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind == JsonValueKind.True
               || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Plex nests the file under Metadata.Media[].Part[].file — dig it out when the flat "file" is absent.</summary>
    private static string? DeepFilePath(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object) return null;
        if (meta.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Array)
            foreach (var md in media.EnumerateArray())
                if (md.TryGetProperty("Part", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var pt in parts.EnumerateArray())
                        if (FirstString(pt, "file") is { } f) return f;
        return null;
    }
}
