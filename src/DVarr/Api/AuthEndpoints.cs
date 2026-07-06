using System.Security.Cryptography;
using System.Text;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

/// <summary>
/// Login-page auth: a stateless signed session cookie ("trusted devices") so the owner signs in
/// once per device instead of being prompted by the browser on every request.
///
/// The cookie (<c>dvarr_session</c>) carries a self-verifying token — no server-side session store:
///   <c>{expiryUnixSeconds}.{base64url(HMACSHA256(serverSecret, expiry + "|" + username))}</c>
/// The server secret (<c>session_signing_key</c>) is 32 random bytes persisted in the Secrets table
/// via the same EnsureXAsync pattern as the Sonarr key / calendar token. Rotating that row logs every
/// device out (the old MACs stop verifying).
///
/// Credentials are the SAME env-configured user/pass the middleware already knows — this class just
/// verifies them with a constant-time compare and, on success, mints the cookie.
/// </summary>
public static class AuthEndpoints
{
    private const string SigningKeySecretName = "session_signing_key";
    public const string CookieName = "dvarr_session";

    // 180-day "remember this device" lifetime (seconds). A session-scoped cookie (no Max-Age) is used
    // when the box is unchecked; the TOKEN still carries this same expiry so the server has a hard cap.
    public const long RememberSeconds = 180L * 86400;

    // ---- per-IP failure limiter (in-memory, best-effort) ----
    private const int MaxFailures = 8;              // >= this many failures inside the window => 429
    private const long WindowSeconds = 10 * 60;     // rolling 10-minute window
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, long WindowStart)> _failures = new();

    public static void MapAuthApi(this WebApplication app)
    {
        // POST /api/auth/login {username,password,remember} -> 200 {ok:true} + Set-Cookie, or 401 {error}, or 429.
        // Exempt from the gate in the middleware (login must be reachable while logged out).
        app.MapPost("/api/auth/login", async (HttpContext ctx, DVarrDbContext db, DbWriteGate gate, IConfiguration config) =>
        {
            var ip = ClientIp(ctx);
            if (IsRateLimited(ip))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsJsonAsync(new { error = "too many attempts, try again later" });
                return;
            }

            LoginRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(); }
            catch { req = null; }  // malformed JSON -> treated as a failed attempt below

            var user = config["DVARR_AUTH_USER"] ?? config["DVarr:AuthUser"] ?? "user";
            var pass = config["DVARR_AUTH_PASS"] ?? config["DVarr:AuthPass"] ?? "password";

            var ok = req is not null
                     && ConstantTimeEquals(Encoding.UTF8.GetBytes(req.Username ?? ""), Encoding.UTF8.GetBytes(user))
                     & ConstantTimeEquals(Encoding.UTF8.GetBytes(req.Password ?? ""), Encoding.UTF8.GetBytes(pass));

            if (!ok)
            {
                RecordFailure(ip);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid username or password" });
                return;
            }

            ClearFailures(ip);
            var key = await EnsureSessionSigningKeyAsync(db, gate);
            var remember = req!.Remember ?? true;
            var expiry = EpochTime.Now() + RememberSeconds;
            var token = MakeToken(key, expiry, user);

            ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Secure = IsHttps(ctx.Request),
                // Persistent ("remember") => 180-day cookie; otherwise session-scoped (Expires unset).
                Expires = remember ? DateTimeOffset.UtcNow.AddSeconds(RememberSeconds) : null,
            });
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        });

        // POST /api/auth/logout -> clear the cookie. Harmless while logged out, so also exempt.
        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Append(CookieName, "", new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Secure = IsHttps(ctx.Request),
                Expires = DateTimeOffset.UnixEpoch,  // in the past => browser drops it
            });
            return Results.Json(new { ok = true });
        });
    }

    private sealed class LoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool? Remember { get; set; }
    }

    /// <summary>Returns the 32-byte session signing key, generating it on first call (hex-persisted in Secrets,
    /// mirroring EnsureApiKeyAsync). Rotating this row invalidates every issued cookie.</summary>
    public static async Task<byte[]> EnsureSessionSigningKeyAsync(DVarrDbContext db, DbWriteGate gate)
    {
        var row = await db.Secrets.FirstOrDefaultAsync(s => s.Name == SigningKeySecretName);
        if (row is not null) return Convert.FromHexString(row.Value);
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(keyBytes).ToLowerInvariant();
        await gate.WriteAsync(async () =>
        {
            db.Secrets.Add(new SecretEntry { Name = SigningKeySecretName, Value = hex, CreatedUtc = EpochTime.Now(), UpdatedUtc = EpochTime.Now() });
            await db.SaveChangesAsync();
        });
        return keyBytes;
    }

    /// <summary>Build the cookie value: {expiry}.{base64url(HMAC(key, expiry + "|" + username))}.</summary>
    public static string MakeToken(byte[] key, long expiry, string username)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(expiry + "|" + username));
        return expiry + "." + Base64UrlEncode(mac);
    }

    /// <summary>
    /// Validate a session cookie value. Returns true only when the token parses, is unexpired, and the recomputed
    /// HMAC matches in constant time. Any malformed input (missing dot, non-numeric expiry, bad base64) returns
    /// false — never throws — so junk cookies are treated as "not authenticated", not a 500.
    /// </summary>
    public static bool ValidateToken(byte[] key, string? token, string expectedUsername)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return false;

        if (!long.TryParse(token.AsSpan(0, dot), out var expiry)) return false;
        if (expiry <= EpochTime.Now()) return false;  // expired (or nonsensical past value)

        byte[] providedMac;
        try { providedMac = Base64UrlDecode(token.Substring(dot + 1)); }
        catch { return false; }

        var expectedMac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(expiry + "|" + expectedUsername));
        // FixedTimeEquals short-circuits on length mismatch, but both operands are fixed 32-byte SHA-256 digests
        // (a truncated/oversized providedMac just fails the length check without leaking anything useful).
        return CryptographicOperations.FixedTimeEquals(providedMac, expectedMac);
    }

    // Treat the request as HTTPS when it arrives directly over TLS OR the nginx proxy marked it so. The LAN http
    // path leaves Secure off so the cookie still travels on plain http.
    private static bool IsHttps(HttpRequest req) =>
        req.IsHttps || string.Equals(req.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);

    // ---- rate limiter --------------------------------------------------------------------------

    /// <summary>
    /// Best-effort client IP for the limiter. The public path is Cloudflare → SWAG/nginx → here, and Cloudflare sets
    /// <c>CF-Connecting-IP</c> to the real client on every proxied request, OVERWRITING any client-supplied value — so
    /// it's the one hop an external attacker can't forge. Prefer it. (The X-Forwarded-For chain is unusable here: nginx
    /// has no Cloudflare real-IP restoration, so XFF's first hop is the raw client-controlled value — rotating it gave
    /// every brute-force attempt its own limiter bucket and defeated the 8/10-min cap; XFF's last hop / X-Real-IP is the
    /// shared CF edge IP, which would instead collapse all external users into one bucket.) Fall back to the
    /// appended-XFF-first-hop only for a direct private/LAN peer (no Cloudflare in that path), then the raw peer. Not
    /// bulletproof against a caller reaching the origin directly, bypassing Cloudflare — that's a separate
    /// origin-exposure concern — but for normal public traffic this rate-limits per real client.
    /// </summary>
    private static string ClientIp(HttpContext ctx)
    {
        // Cloudflare-authoritative real client IP: present on every CF-proxied request, and CF replaces any value a
        // client tries to send, so it can't be spoofed from the outside to dodge or frame a bucket.
        var cf = ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cf)) return cf.Trim();

        var peer = ctx.Connection.RemoteIpAddress;
        var peerStr = peer?.ToString() ?? "unknown";
        if (peer is not null && IsPrivate(peer))
        {
            var xff = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(xff))
            {
                var first = xff.Split(',')[0].Trim();
                if (first.Length > 0) return first;
            }
        }
        return peerStr;
    }

    private static bool IsPrivate(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }
        // IPv6 unique-local (fc00::/7) or link-local (fe80::/10).
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        return false;
    }

    private static bool IsRateLimited(string ip)
    {
        if (!_failures.TryGetValue(ip, out var e)) return false;
        var now = EpochTime.Now();
        if (now - e.WindowStart >= WindowSeconds) return false;  // window elapsed — stale, treat as clear
        return e.Count >= MaxFailures;
    }

    private static void RecordFailure(string ip)
    {
        var now = EpochTime.Now();
        _failures.AddOrUpdate(ip,
            _ => (1, now),
            (_, e) => (now - e.WindowStart >= WindowSeconds) ? (1, now) : (e.Count + 1, e.WindowStart));
    }

    private static void ClearFailures(string ip) => _failures.TryRemove(ip, out _);

    // ---- helpers -------------------------------------------------------------------------------

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        // Same length-tolerant constant-time compare as BasicAuthMiddleware: MAC each side with a random per-call
        // key so equal (length AND bytes) inputs produce equal 32-byte digests and any difference does not, with
        // no branch on length and no exception path.
        Span<byte> key = stackalloc byte[32];
        RandomNumberGenerator.Fill(key);
        Span<byte> macA = stackalloc byte[32];
        Span<byte> macB = stackalloc byte[32];
        HMACSHA256.HashData(key, a, macA);
        HMACSHA256.HashData(key, b, macB);
        return CryptographicOperations.FixedTimeEquals(macA, macB);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
        return Convert.FromBase64String(t);
    }
}
