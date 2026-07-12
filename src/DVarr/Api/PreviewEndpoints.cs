using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Services.Ingest;
using DVarr.Services.Tuner;
using Microsoft.AspNetCore.Http.Features;

namespace DVarr.Api;

/// <summary>
/// Live in-browser preview. Unlike the M3U export proxy (which 302-redirects, exposing the provider URL to the
/// follower), this STREAMS the provider .ts through DVarr so credentials never reach the browser — mpegts.js on
/// the client consumes it directly. A preview consumes the credential's ONE stream slot, so it leases like a
/// recording and returns 409 when the slot is busy; the lease is held for the life of the HTTP connection and
/// released when the player closes (fetch abort) or a safety cap elapses, so a forgotten tab can't hold the slot.
/// </summary>
public static class PreviewEndpoints
{
    public static void MapPreviewApi(this WebApplication app)
    {
        app.MapGet("/api/preview/{channelId:int}.ts", async (int channelId, HttpContext ctx,
            DVarrDbContext db, XtreamClient xtream, TunerLeaseManager tuner, IHttpClientFactory httpFactory, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Preview");
            var ch = await db.Channels.FindAsync(channelId);
            if (ch is null) { ctx.Response.StatusCode = 404; return; }
            var src = await db.Sources.FindAsync(ch.SourceId);
            if (src is null) { ctx.Response.StatusCode = 404; return; }
            // Off-limits guard: never stream from a disabled source.
            if (!src.Enabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "source_disabled", message = $"{src.Label} is disabled (off-limits)." }); return; }

            var directUrl = ch.DirectUrl;
            var upstreamUrl = !string.IsNullOrWhiteSpace(directUrl) ? directUrl! : xtream.StreamTsUrl(src, ch.StreamId);
            var needsLease = string.IsNullOrWhiteSpace(directUrl); // a provider stream uses the 1 slot; a DirectUrl/test channel does not

            TunerLease? lease = null;
            if (needsLease)
            {
                lease = await tuner.TryAcquireAsync(src.Id, LeasePurpose.Live, null, ch.Id, ch.StreamId, ctx.RequestAborted);
                if (lease is null)
                {
                    ctx.Response.StatusCode = 409;
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        error = "stream_busy",
                        message = $"{src.Label}'s single stream is in use (a recording or another preview). Close it and try again.",
                    });
                    return;
                }
            }

            // Primary release is the client closing the connection (RequestAborted). The 3h cap is a backstop so a
            // crashed/forgotten tab eventually frees the credential's slot.
            using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            capCts.CancelAfter(TimeSpan.FromHours(3));
            var ct = capCts.Token;

            var client = httpFactory.CreateClient("preview");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
                // Always send a player UA (issue #8): a blank Source.UserAgent must fall back to the same VLC UA the
                // recorder and discovery use — providers routinely 4xx a request with no recognisable player UA, which
                // made preview the ONLY provider call that failed while ingest and recording worked.
                req.Headers.TryAddWithoutValidation("User-Agent",
                    string.IsNullOrWhiteSpace(src.UserAgent) ? XtreamClient.DefaultUserAgent : src.UserAgent);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // Pass the provider's real status through (clamped to an error class) so the player surfaces
                    // e.g. [403] instead of a generic 502 the UI can't explain.
                    var status = (int)resp.StatusCode;
                    ctx.Response.StatusCode = status >= 400 ? status : 502;
                    log.LogInformation("[Preview] upstream {Status} for channel {Id} ({Url})", status, channelId, upstreamUrl);
                    await ctx.Response.WriteAsJsonAsync(new { error = "upstream", status });
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.Headers.ContentType = "video/mp2t";
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers.Append("X-Accel-Buffering", "no"); // don't let any reverse proxy buffer a live stream
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

                await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
                await upstream.CopyToAsync(ctx.Response.Body, 64 * 1024, ct);
            }
            catch (OperationCanceledException) { /* client closed the player, or the safety cap elapsed */ }
            catch (Exception ex) { log.LogDebug(ex, "[Preview] stream error for channel {Id}", channelId); }
            finally
            {
                if (lease is not null) await tuner.ReleaseAsync(lease, CancellationToken.None);
            }
        });

        // ---- HLS transcode fallback (for HEVC/AC-3/etc. the browser can't decode directly) ----
        app.MapGet("/api/preview/{channelId:int}/hls/index.m3u8", async (int channelId, HttpContext ctx, DVarr.Services.Recording.PreviewTranscodeManager mgr) =>
        {
            var r = await mgr.EnsureAsync(channelId, ctx.RequestAborted);
            switch (r.Status)
            {
                case DVarr.Services.Recording.PreviewStatus.Ok:
                    var m3u8 = await File.ReadAllTextAsync(r.PlaylistPath!, ctx.RequestAborted);
                    return Results.Text(m3u8, "application/vnd.apple.mpegurl");
                case DVarr.Services.Recording.PreviewStatus.Busy:
                    return Results.Json(new { error = "stream_busy", message = "This credential's single stream is in use." }, statusCode: 409);
                case DVarr.Services.Recording.PreviewStatus.Disabled:
                    return Results.Json(new { error = "source_disabled" }, statusCode: 403);
                case DVarr.Services.Recording.PreviewStatus.NotFound:
                    return Results.NotFound();
                default:
                    return Results.Json(new { error = "transcode_failed", message = "Could not start the transcode (channel offline?)." }, statusCode: 502);
            }
        });

        // Segments (and playlist re-reads) for the transcode session. Path-guarded inside the manager.
        app.MapGet("/api/preview/{channelId:int}/hls/{seg}", (int channelId, string seg, DVarr.Services.Recording.PreviewTranscodeManager mgr) =>
        {
            var path = mgr.GetFilePath(channelId, seg);
            if (path is null) return Results.NotFound();
            var ctype = seg.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "application/vnd.apple.mpegurl" : "video/mp2t";
            return Results.File(path, ctype);
        });
    }
}
