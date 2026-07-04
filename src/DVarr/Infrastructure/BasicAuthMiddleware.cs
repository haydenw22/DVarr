using System.Security.Cryptography;
using System.Text;

namespace DVarr.Infrastructure;

/// <summary>
/// Gates the entire site behind HTTP Basic auth (DVarr is now publicly exposed at
/// dvarr.whittledigitalsolutions.com behind an nginx TLS-terminating reverse proxy).
///
/// Registered BEFORE UseDefaultFiles/UseStaticFiles in Program.cs so the SPA shell, every static
/// asset, and all /api/* routes are protected by a single prompt — browsers cache the credentials for
/// the session, so fetch/XHR/SSE/preview streams and the PWA/service worker all inherit them afterwards.
///
/// Credentials come from configuration (env vars flow into IConfiguration automatically):
///   DVARR_AUTH_USER / DVARR_AUTH_PASS   (primary; set these in the compose .env)
///   DVarr:AuthUser  / DVarr:AuthPass    (config-key aliases)
/// defaulting to user / password.
///
/// A curated exempt list keeps machine-to-machine surfaces working WITHOUT basic auth — each entry
/// carries a credential of its own or is a credential-free LAN-only surface (see comments below).
/// </summary>
public sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _userBytes;
    private readonly byte[] _passBytes;

    public BasicAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;

        // Primary env-var names first; the DVarr: config-section keys are trivial aliases.
        var user = config["DVARR_AUTH_USER"] ?? config["DVarr:AuthUser"] ?? "user";
        var pass = config["DVARR_AUTH_PASS"] ?? config["DVarr:AuthPass"] ?? "password";

        _userBytes = Encoding.UTF8.GetBytes(user);
        _passBytes = Encoding.UTF8.GetBytes(pass);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExempt(context.Request.Path) || IsAuthorized(context.Request))
        {
            await _next(context);
            return;
        }

        // Missing/wrong credentials → challenge. charset="UTF-8" tells the browser to send UTF-8 bytes.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"DVarr\", charset=\"UTF-8\"";
    }

    /// <summary>
    /// Machine-to-machine surfaces that must keep working without the new basic auth. Matched against the
    /// canonical <see cref="PathString"/> value (already URL-decoded and collapsed of empty segments by the
    /// ASP.NET pipeline), so case tricks (Basic auth realm is case-sensitive but paths here are compared
    /// OrdinalIgnoreCase) and "//" double-slash tricks can't smuggle a protected path past the check.
    /// </summary>
    private static bool IsExempt(PathString path)
    {
        // PathString.Value is the canonical, decoded path. Guard against a null value (e.g. request to "*").
        var p = path.Value ?? string.Empty;

        // The Docker HEALTHCHECK runs `wget http://localhost:1867/api/health` inside the container with no
        // credentials; gating it would flip the container to "unhealthy". Exact-ish prefix is fine.
        if (p.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase)) return true;

        // Google Calendar fetches the .ics server-side and cannot present basic auth; the endpoint carries
        // its OWN capability token (401s without it), so basic auth here would double-lock a working feed.
        if (p.StartsWith("/api/calendar.ics", StringComparison.OrdinalIgnoreCase)) return true;

        // Plex Custom Metadata Provider: the LAN Plex server calls /plex (302) and /api/plex/* and can't send
        // basic auth. Public sports metadata only — never the IPTV provider — so no secret is exposed.
        if (p.StartsWith("/plex", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/api/plex/", StringComparison.OrdinalIgnoreCase)) return true;

        // Sonarr-emulation surface for Prowlarr; already guarded by its own X-Api-Key constant-time check.
        if (p.StartsWith("/api/v3/", StringComparison.OrdinalIgnoreCase)) return true;

        // Credential-free M3U/XMLTV export for LAN IPTV players (no login travels in the exported playlist).
        if (p.StartsWith("/api/iptv/", StringComparison.OrdinalIgnoreCase)) return true;

        // Home Assistant polls this REST status endpoint without credentials.
        if (p.StartsWith("/api/ha/status", StringComparison.OrdinalIgnoreCase)) return true;

        // EXACT /api/stream/{digits}.ts only: LAN IPTV players pull the stream proxy with no headers (already
        // 403-blocked externally at nginx). Deliberately NOT a prefix — /api/stream/recordings (the UI's SSE)
        // must stay gated so it prompts once; the browser then attaches cached basic creds to the same-origin
        // EventSource automatically and it keeps working for the logged-in user.
        if (StreamTsRegex.IsMatch(p)) return true;

        return false;
    }

    // ^/api/stream/<digits>.ts$ — anchored so nothing before/after can widen it.
    private static readonly System.Text.RegularExpressions.Regex StreamTsRegex =
        new(@"^/api/stream/[0-9]+\.ts$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private bool IsAuthorized(HttpRequest request)
    {
        string? header = request.Headers.Authorization;
        if (string.IsNullOrEmpty(header)) return false;

        // Expect "Basic <base64(user:pass)>". Scheme is case-insensitive per RFC 7617.
        const string prefix = "Basic ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var encoded = header.Substring(prefix.Length).Trim();
        if (encoded.Length == 0) return false;

        // Parse defensively: malformed base64 must yield 401, never a 500.
        byte[] decoded;
        try { decoded = Convert.FromBase64String(encoded); }
        catch (FormatException) { return false; }

        string credentials;
        try { credentials = Encoding.UTF8.GetString(decoded); }
        catch { return false; }

        // Only split on the FIRST colon — the password may itself contain colons.
        var sep = credentials.IndexOf(':');
        if (sep < 0) return false;

        var providedUser = Encoding.UTF8.GetBytes(credentials.Substring(0, sep));
        var providedPass = Encoding.UTF8.GetBytes(credentials.Substring(sep + 1));

        // Both compared in constant time (house pattern from ParityEndpoints' API-key check). See ConstantTimeEquals
        // for how length differences are handled without leaking length via an early return or an exception.
        return ConstantTimeEquals(providedUser, _userBytes) & ConstantTimeEquals(providedPass, _passBytes);
    }

    /// <summary>
    /// Constant-time byte compare that also tolerates length differences. CryptographicOperations.FixedTimeEquals
    /// throws / short-circuits when the two spans differ in length, which would both risk a 500 and leak the
    /// expected length via timing. To avoid that we FixedTimeEquals each candidate against a fixed-length HMAC of
    /// itself keyed by the expected value: equal inputs (same length AND same bytes) produce equal MACs; any
    /// difference — including a length mismatch — produces unequal MACs, and every comparison runs over the same
    /// 32-byte digest regardless of input length. No branch on length, no exception path.
    /// </summary>
    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        // Derive a random per-call key so the MACs can't be precomputed offline.
        Span<byte> key = stackalloc byte[32];
        RandomNumberGenerator.Fill(key);
        Span<byte> macA = stackalloc byte[32];
        Span<byte> macB = stackalloc byte[32];
        HMACSHA256.HashData(key, a, macA);
        HMACSHA256.HashData(key, b, macB);
        return CryptographicOperations.FixedTimeEquals(macA, macB);
    }
}
