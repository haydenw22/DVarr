using System.Security.Cryptography;
using System.Text;
using DVarr.Api;
using DVarr.Data;

namespace DVarr.Infrastructure;

/// <summary>
/// Gates the entire site (designed to be safe even when DVarr is exposed publicly behind an nginx
/// TLS-terminating reverse proxy). Two ways in:
///   1. a valid <c>dvarr_session</c> cookie — the "trusted device" login-page path (see <see cref="AuthEndpoints"/>);
///   2. a valid HTTP <c>Basic</c> header — kept so curl/scripts and the M2M callers that already send it are unchanged.
///
/// Registered BEFORE UseDefaultFiles/UseStaticFiles in Program.cs so the SPA shell, every static asset, and all
/// /api/* routes are protected. Unlike the old behaviour, a missing/invalid credential no longer emits a
/// WWW-Authenticate challenge (which made the browser pop its native prompt on every launch). Instead:
///   * a browser navigation for HTML (GET + Accept: text/html) is 302-redirected to /login.html;
///   * everything else gets a plain 401 JSON body and NO WWW-Authenticate (so the SPA's fetch layer can catch it).
///
/// Credentials come from configuration (env vars flow into IConfiguration automatically):
///   DVARR_AUTH_USER / DVARR_AUTH_PASS   (primary; set these in the compose .env)
///   DVarr:AuthUser  / DVarr:AuthPass    (config-key aliases)
/// defaulting to user / password.
///
/// A curated exempt list keeps machine-to-machine surfaces working WITHOUT auth — each entry carries a credential
/// of its own or is a credential-free LAN-only surface (see comments below). /login.html and /api/auth/ are exempt
/// too so the login page can load and post while logged out.
/// </summary>
public sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _user;
    private readonly byte[] _userBytes;
    private readonly byte[] _passBytes;

    // Session signing key, resolved lazily from the Secrets table on the first request and cached for the process
    // lifetime. Program.cs ensures the row exists at startup, so this read normally hits an existing secret.
    private byte[]? _signingKey;

    public BasicAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;

        // Primary env-var names first; the DVarr: config-section keys are trivial aliases.
        _user = config["DVARR_AUTH_USER"] ?? config["DVarr:AuthUser"] ?? "user";
        var pass = config["DVARR_AUTH_PASS"] ?? config["DVarr:AuthPass"] ?? "password";

        _userBytes = Encoding.UTF8.GetBytes(_user);
        _passBytes = Encoding.UTF8.GetBytes(pass);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        // Decision order (documented in the PR): exempt surface -> valid session cookie -> valid Basic header.
        if (IsExempt(request.Path) || await HasValidSessionAsync(context) || IsAuthorizedBasic(request))
        {
            await _next(context);
            return;
        }

        // Not authenticated. A browser navigating to a page should land on the login screen; anything else
        // (fetch/XHR/SSE/M2M) gets a clean 401 with no WWW-Authenticate — the SPA's api helper redirects on it.
        if (IsHtmlNavigation(request))
        {
            context.Response.Redirect("/login.html");  // 302
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "authentication required" });
    }

    // GET whose Accept advertises HTML => a top-level browser navigation (address bar / link / PWA launch).
    private static bool IsHtmlNavigation(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        && (request.Headers.Accept.FirstOrDefault()?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Machine-to-machine surfaces (plus the login page/endpoints) that must work without the session/basic auth.
    /// Matched against the canonical <see cref="PathString"/> value (already URL-decoded and collapsed of empty
    /// segments by the ASP.NET pipeline), compared OrdinalIgnoreCase, so "//" double-slash and case tricks can't
    /// smuggle a protected path past the check.
    /// </summary>
    private static bool IsExempt(PathString path)
    {
        // PathString.Value is the canonical, decoded path. Guard against a null value (e.g. request to "*").
        var p = path.Value ?? string.Empty;

        // The login page itself must render while logged out. It inlines ALL its css/js so this single exact path is
        // the only static asset that needs exempting (no /js/*, /css/*, or logo request travels before login).
        if (string.Equals(p, "/login.html", StringComparison.OrdinalIgnoreCase)) return true;

        // Login/logout endpoints: login is obviously pre-auth; logout is harmless.
        if (p.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)) return true;

        // The Docker HEALTHCHECK runs `wget http://localhost:1867/api/health` inside the container with no
        // credentials; gating it would flip the container to "unhealthy". Exact-ish prefix is fine.
        if (p.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase)) return true;

        // Google Calendar fetches the .ics server-side and cannot present auth; the endpoint carries its OWN
        // capability token (401s without it), so gating here would double-lock a working feed.
        if (p.StartsWith("/api/calendar.ics", StringComparison.OrdinalIgnoreCase)) return true;

        // Plex Custom Metadata Provider: the LAN Plex server calls /plex (302) and /api/plex/* and can't send
        // auth. Public sports metadata only — never the IPTV provider — so no secret is exposed.
        if (p.StartsWith("/plex", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/api/plex/", StringComparison.OrdinalIgnoreCase)) return true;

        // Sonarr-emulation surface for Prowlarr; already guarded by its own X-Api-Key constant-time check.
        if (p.StartsWith("/api/v3/", StringComparison.OrdinalIgnoreCase)) return true;

        // Credential-free M3U/XMLTV export for LAN IPTV players (no login travels in the exported playlist).
        if (p.StartsWith("/api/iptv/", StringComparison.OrdinalIgnoreCase)) return true;

        // Home Assistant polls this REST status endpoint without credentials.
        if (p.StartsWith("/api/ha/status", StringComparison.OrdinalIgnoreCase)) return true;

        // Media-server "watched" webhooks (Plex/Jellyfin): a media server can't present DVarr's login, so these
        // two EXACT endpoints are login-exempt — but each carries its OWN per-install secret token (401 without
        // it), mirroring the calendar feed. Deliberately NOT a prefix (audit SEC-08): a future webhook endpoint
        // must opt in here explicitly rather than being born unauthenticated.
        if (p.Equals("/api/webhooks/plex", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api/webhooks/jellyfin", StringComparison.OrdinalIgnoreCase)) return true;

        // EXACT /api/stream/{digits}.ts only: LAN IPTV players pull the stream proxy with no headers (already
        // 403-blocked externally at nginx). Deliberately NOT a prefix — /api/stream/recordings (the UI's SSE)
        // must stay gated so the logged-in browser attaches its session cookie / cached basic creds automatically.
        if (StreamTsRegex.IsMatch(p)) return true;

        return false;
    }

    // ^/api/stream/<digits>.ts$ — anchored so nothing before/after can widen it.
    private static readonly System.Text.RegularExpressions.Regex StreamTsRegex =
        new(@"^/api/stream/[0-9]+\.ts$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// True when the request carries a valid <c>dvarr_session</c> cookie. Resolves the signing key lazily (once) from
    /// the Secrets table via the request scope. Any failure to obtain the key or any malformed cookie is treated as
    /// "no session" — never an exception that would 500 the request.
    /// </summary>
    private async Task<bool> HasValidSessionAsync(HttpContext context)
    {
        var cookie = context.Request.Cookies[AuthEndpoints.CookieName];
        if (string.IsNullOrEmpty(cookie)) return false;

        var key = _signingKey;
        if (key is null)
        {
            try
            {
                var db = context.RequestServices.GetRequiredService<DVarrDbContext>();
                var gate = context.RequestServices.GetRequiredService<DbWriteGate>();
                key = await AuthEndpoints.EnsureSessionSigningKeyAsync(db, gate);
                _signingKey = key;  // cache for the rest of the process
            }
            catch
            {
                return false;  // couldn't load the key — fail closed (redirect to login), don't 500
            }
        }

        return AuthEndpoints.ValidateToken(key, cookie, _user);
    }

    private bool IsAuthorizedBasic(HttpRequest request)
    {
        string? header = request.Headers.Authorization;
        if (string.IsNullOrEmpty(header)) return false;

        // Expect "Basic <base64(user:pass)>". Scheme is case-insensitive per RFC 7617.
        const string prefix = "Basic ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var encoded = header.Substring(prefix.Length).Trim();
        if (encoded.Length == 0) return false;

        // Parse defensively: malformed base64 must yield "unauthorized", never a 500.
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

        // Both compared in constant time (house pattern from ParityEndpoints' API-key check).
        return ConstantTimeEquals(providedUser, _userBytes) & ConstantTimeEquals(providedPass, _passBytes);
    }

    /// <summary>
    /// Constant-time byte compare that also tolerates length differences. CryptographicOperations.FixedTimeEquals
    /// throws / short-circuits when the two spans differ in length, which would both risk a 500 and leak the
    /// expected length via timing. To avoid that we FixedTimeEquals each candidate against a fixed-length HMAC of
    /// itself keyed by a random per-call key: equal inputs (same length AND same bytes) produce equal MACs; any
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
