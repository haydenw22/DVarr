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
/// (audit SEC-08): without it every request is 401 and nothing is written. Matching is path-based and refuses
/// ambiguity — an unauthenticated or sloppy payload can never feed the auto-delete plan.
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
                        var n = await MarkWatchedAsync(db, gate, path, log);
                        log.LogInformation("[Webhook] plex scrobble '{Title}' → {N} item(s) marked watched", title ?? "?", n);
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
                        var n = await MarkWatchedAsync(db, gate, path, log);
                        log.LogInformation("[Webhook] jellyfin '{Title}' → {N} item(s) marked watched", title ?? "?", n);
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

    /// <summary>Mark the library item at <paramref name="path"/> watched. Exact path first, then a filename match
    /// (a media server's mount path often differs from DVarr's) — but ONLY when the filename resolves to exactly one
    /// row: a basename shared by several items is refused rather than guessed at (audit SEC-08), because watched
    /// state can feed the auto-delete plan. Path-only — a title match is too ambiguous to base an auto-delete on.
    /// Returns how many rows were flagged.</summary>
    private static async Task<int> MarkWatchedAsync(DVarrDbContext db, DbWriteGate gate, string? path, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        var ids = await db.LibraryItems.Where(i => i.FilePath == path).Select(i => i.Id).ToListAsync();
        if (ids.Count == 0)
        {
            var name = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                ids = await db.LibraryItems.Where(i => i.FilePath.EndsWith(name)).Select(i => i.Id).ToListAsync();
                if (ids.Count > 1)
                {
                    log.LogWarning("[Webhook] '{Name}' matches {N} library files — ambiguous, refusing to mark any watched", name, ids.Count);
                    return 0;
                }
            }
        }
        if (ids.Count == 0) return 0;
        var now = EpochTime.Now();
        await gate.WriteAsync(async () =>
            await db.LibraryItems.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.WatchedUtc, now)));
        return ids.Count;
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
