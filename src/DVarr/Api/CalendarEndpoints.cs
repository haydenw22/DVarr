using System.Text;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Events;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Api;

/// <summary>
/// A subscribable iCalendar (ICS) feed of upcoming, monitored, followed events — point Google/Apple Calendar at
/// <c>/api/calendar.ics?token=…</c> and it polls every ~6h. Token-gated (a persisted 32-hex secret, constant-time
/// compared like the Sonarr key); the response is cached in-memory for 5 minutes so a calendar client's polling never
/// hammers the DB. The event set honours the same follow filters as the calendar page (team-follow / motorsport
/// session-follow / manually-locked-monitored always in).
/// </summary>
public static class CalendarEndpoints
{
    private const string TokenSecretName = "calendar_token";
    private const int CacheTtlS = 300;       // 5-min in-memory response cache — Google polls hard; never hit the DB per poll
    private const long PastWindowS = 6 * 3600;   // include events that started up to 6h ago (in-progress / just finished)
    private const long FutureWindowS = 60L * 86400; // …through 60 days out

    // Process-wide cached feed (the feed is identical for every subscriber — it's token-gated, not per-user).
    private static readonly object _cacheLock = new();
    private static string? _cachedBody;
    private static long _cachedAtUtc;

    public static void MapCalendarApi(this WebApplication app)
    {
        // The subscribable feed. Token-gated; bad/missing token → 401. Response is text/calendar with a 5-min cache.
        app.MapGet("/api/calendar.ics", async (HttpContext ctx, DVarrDbContext db) =>
        {
            if (!await TokenValidAsync(ctx, db)) return Results.Unauthorized();

            var now = EpochTime.Now();
            string body;
            lock (_cacheLock)
            {
                if (_cachedBody is not null && now - _cachedAtUtc < CacheTtlS)
                    return Results.Text(_cachedBody, "text/calendar; charset=utf-8");
            }
            body = await BuildIcsAsync(db, now);
            lock (_cacheLock) { _cachedBody = body; _cachedAtUtc = now; }
            return Results.Text(body, "text/calendar; charset=utf-8");
        });

        // The copy-me URL (relative path + token) for the owner/UI. LAN-open like the rest of the API.
        app.MapGet("/api/calendar/url", async (DVarrDbContext db, DbWriteGate gate) =>
        {
            var (token, _) = await EnsureCalendarTokenAsync(db, gate);
            return Results.Json(new { url = $"/api/calendar.ics?token={token}" });
        });
    }

    /// <summary>Returns the calendar-feed token, generating a 32-hex secret on first call. <c>Created</c> is true only on
    /// the boot that generated it, so the caller can avoid echoing the secret into the log on every subsequent boot.
    /// Mirrors <see cref="ParityEndpoints.EnsureApiKeyAsync"/> (Secrets storage).</summary>
    public static async Task<(string Token, bool Created)> EnsureCalendarTokenAsync(DVarrDbContext db, DbWriteGate gate)
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

    private static async Task<bool> TokenValidAsync(HttpContext ctx, DVarrDbContext db)
    {
        var provided = ctx.Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided)) return false;
        var token = (await db.Secrets.FirstOrDefaultAsync(s => s.Name == TokenSecretName))?.Value;
        if (string.IsNullOrEmpty(token)) return false;
        // Constant-time compare so the token can't be recovered byte-by-byte via response-timing analysis.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Build the VCALENDAR: one VEVENT per upcoming, monitored, followed event in the window, honouring the same
    /// follow filters as the calendar page (reuses LeagueEndpoints.EventFollowed + AutoScheduleService parsers).</summary>
    private static async Task<string> BuildIcsAsync(DVarrDbContext db, long now)
    {
        var winStart = now - PastWindowS;
        var winEnd = now + FutureWindowS;

        // Only MONITORED leagues' events; bounded time window; generous SQL cap (the follow filter runs in memory below).
        var rows = await (from e in db.Events
                          join l in db.Leagues on e.LeagueId equals l.Id
                          where l.Monitored && e.StartUtc >= winStart && e.StartUtc <= winEnd
                          orderby e.StartUtc
                          select new { e, league = l.Name, sport = l.Sport })
                          .Take(10000).ToListAsync();

        if (rows.Count > 0)
        {
            // Same follow filter as GET /api/events: team-follow (any sport) + session-follow (motorsport only, since
            // every non-motorsport title classifies as "Race"); a MonitoredLocked+Monitored event always stays in.
            var ids = rows.Select(x => x.e.LeagueId).Distinct().ToList();
            var follow = await db.Leagues.Where(l => ids.Contains(l.Id))
                .Select(l => new { l.Id, l.Sport, l.MonitoredTeamsJson, l.MonitoredSessionsJson }).ToListAsync();
            var teamSets = follow.Select(f => (f.Id, Set: AutoScheduleService.ParseMonitoredTeamIds(f.MonitoredTeamsJson)))
                .Where(x => x.Set.Count > 0).ToDictionary(x => x.Id, x => x.Set);
            var sessionSets = follow.Where(f => MotorsportSession.IsMotorsport(f.Sport))
                .Select(f => (f.Id, Set: AutoScheduleService.ParseMonitoredSessions(f.MonitoredSessionsJson)))
                .Where(x => x.Set.Count > 0).ToDictionary(x => x.Id, x => x.Set);
            if (teamSets.Count > 0 || sessionSets.Count > 0)
                rows = rows.Where(x => LeagueEndpoints.EventFollowed(x.e, teamSets, sessionSets)).ToList();
        }

        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n"); // CRLF line endings throughout (RFC5545)

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//DVarr//Sports DVR//EN");
        Line("CALSCALE:GREGORIAN");
        Line("X-WR-CALNAME:DVarr Sports");
        Line("X-PUBLISHED-TTL:PT6H");
        Line("REFRESH-INTERVAL;VALUE=DURATION:PT6H");

        var stamp = Ics(now);
        foreach (var x in rows)
        {
            var e = x.e;
            var end = e.EndUtc ?? e.StartUtc + 7200; // DTEND = EndUtc, else start + 2h
            Line("BEGIN:VEVENT");
            foreach (var l in Fold($"UID:dvarr-event-{e.Id}@dvarr")) Line(l);
            Line($"DTSTAMP:{stamp}");
            Line($"DTSTART:{Ics(e.StartUtc)}");
            Line($"DTEND:{Ics(end)}");
            foreach (var l in Fold($"SUMMARY:{Esc($"{x.league}: {e.Title}")}")) Line(l);
            foreach (var l in Fold($"CATEGORIES:{Esc(x.sport)}")) Line(l);
            Line("BEGIN:VALARM");
            Line("ACTION:DISPLAY");
            Line("DESCRIPTION:Reminder");
            Line("TRIGGER:-PT30M");
            Line("END:VALARM");
            Line("END:VEVENT");
        }

        Line("END:VCALENDAR");
        return sb.ToString();
    }

    // UTC basic format per RFC5545 (yyyyMMddTHHmmssZ).
    private static string Ics(long epoch) => EpochTime.ToUtc(epoch).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

    // RFC5545 TEXT escaping: backslash, semicolon, comma, and newline (CR/LF collapsed to the literal \n sequence).
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
                .Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
    }

    // Content-line folding: no line may exceed 75 octets. Split on octet boundaries (UTF-8) and continue with CRLF + a
    // single leading space, per RFC5545 §3.1. Returns the already-folded pieces (each WITHOUT its own CRLF — the caller
    // emits one per piece). The continuation space is added here so re-joining with CRLF yields a valid folded line.
    private static IEnumerable<string> Fold(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75) { yield return line; yield break; }
        var pieces = new List<string>();
        var start = 0;
        var first = true;
        while (start < bytes.Length)
        {
            // First line may be 75 octets; continuation lines start with a space, so their content budget is 74.
            var budget = first ? 75 : 74;
            var take = Math.Min(budget, bytes.Length - start);
            // Don't split a multi-byte UTF-8 sequence: back off until the next byte isn't a continuation byte (10xxxxxx).
            while (take > 1 && start + take < bytes.Length && (bytes[start + take] & 0xC0) == 0x80) take--;
            var chunk = Encoding.UTF8.GetString(bytes, start, take);
            yield return first ? chunk : " " + chunk;
            start += take;
            first = false;
        }
    }
}
