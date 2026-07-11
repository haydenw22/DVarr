using System.Globalization;
using DVarr.Data.Entities;
using DVarr.Infrastructure;

namespace DVarr.Services.Events;

/// <summary>A normalised event. ExternalId is provider-stable (drives the churn-proof natural key). Carries
/// optional TheSportsDB enrichment (thumbnail, round, season) for Plex-clean media import + the Plex provider.</summary>
public sealed record IngestedEvent(string ExternalId, string Title, long StartUtc, long? EndUtc, bool DateOnly, string? Status,
    string? ThumbUrl = null, int? Round = null, string? Season = null, string? HomeTeamId = null, string? AwayTeamId = null);

/// <summary>
/// Fetches a league's events. TheSportsDB (free key) is the only provider offered now; legacy "ics" leagues
/// still parse via <see cref="IcsParser"/> for any rows created before the change. Both fail soft (return empty,
/// caller logs); the churn-proof upsert + natural keys (docs/06) absorb provider instability.
/// </summary>
public sealed class EventFetcher
{
    private readonly TheSportsDbClient _tsdb;
    private readonly HttpClient _http;
    private readonly ILogger<EventFetcher> _log;

    public EventFetcher(TheSportsDbClient tsdb, HttpClient http, ILogger<EventFetcher> log) { _tsdb = tsdb; _http = http; _log = log; }

    public Task<List<IngestedEvent>> FetchAsync(League league, CancellationToken ct) => league.EventProvider switch
    {
        "thesportsdb" => FetchTsdbAsync(league, ct),
        "ics" => FetchIcsAsync(league, ct),
        _ => Task.FromResult(new List<IngestedEvent>()),
    };

    private async Task<List<IngestedEvent>> FetchTsdbAsync(League l, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(l.ExternalLeagueId)) return new();
        var leagueId = l.ExternalLeagueId!;
        var okBefore = _tsdb.HttpOkCount; // to tell "provider unreachable" from "reachable but no events" at the end
        var byId = new Dictionary<string, IngestedEvent>(StringComparer.Ordinal); // dedupe by stable idEvent; first writer wins

        void Merge(IReadOnlyList<TsdbEvent> evs)
        {
            foreach (var e in evs)
            {
                if (e.StartUtc is not { } st) continue;
                // TheSportsDB never returns an end time — leave it null here; EventIngestService fills in the
                // per-sport default duration so it's tunable in one place. Carry the team ids for team-follow.
                byId.TryAdd(e.Id, new IngestedEvent(e.Id, e.Title, st, null, e.DateOnly, e.Status,
                    e.Thumb ?? e.Poster, e.Round, e.Season, e.HomeTeamId, e.AwayTeamId));
            }
        }

        // Premium v2: /schedule/league/{id}/{season} returns the COMPLETE season in one call, so the old free-key
        // workaround (capped-season seed + per-day sweep + idEvent gap-fill, ~60 calls that still dropped games) is
        // gone. Take the first non-empty season (calendar-year, then split-year formats — don't mix adjacent seasons),
        // then best-effort merge the NEXT season so fixtures past the season boundary are still mapped. The full set is
        // what makes the chronological episode ordinal correct and the manual-import list complete.
        var year = EpochTime.ToDisplay(EpochTime.Now()).Year;
        string? hitSeason = null;
        foreach (var season in new[] { year.ToString(), $"{year}-{year + 1}", $"{year - 1}-{year}" })
        {
            var evs = await _tsdb.GetSeasonEventsAsync(leagueId, season, ct);
            if (evs.Count == 0) continue;
            Merge(evs);
            hitSeason = season;
            break;
        }
        if (hitSeason is not null)
        {
            // Derive the NEXT season from the one we actually matched, not the calendar year — else a split-year league
            // matched via "{year-1}-{year}" (e.g. "2025-2026") would skip a whole year ("2027-2028" instead of "2026-2027").
            string next;
            if (hitSeason.Contains('-') && int.TryParse(hitSeason.Split('-')[1], out var end)) next = $"{end}-{end + 1}";
            else if (int.TryParse(hitSeason, out var y)) next = (y + 1).ToString();
            else next = (year + 1).ToString();
            try { Merge(await _tsdb.GetSeasonEventsAsync(leagueId, next, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "[Events] next-season pull {Season} failed for league {Id}", next, l.Id); }
        }

        // If NOTHING succeeded (every call failed / 429-exhausted) and we have no events, the provider is unreachable —
        // throw so SyncLeagueAsync reports failure and does NOT advance LastEventSyncUtc (which would otherwise suppress
        // auto-retry for the whole sync interval). A genuinely empty league still returns [] (its calls succeeded).
        if (byId.Count == 0 && _tsdb.HttpOkCount == okBefore)
            throw new InvalidOperationException($"TheSportsDB unreachable for league {leagueId} (all calls failed)");

        _log.LogInformation("[Events] TheSportsDB league {Id} ({Ext}): {Count} events (full season {Season})",
            l.Id, leagueId, byId.Count, hitSeason ?? "none");
        return byId.Values.ToList();
    }

    // ---- ICS calendar feed (legacy; not offered for new leagues) ----
    private async Task<List<IngestedEvent>> FetchIcsAsync(League l, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(l.IcsUrl)) return new();
        try { var text = await _http.GetStringAsync(l.IcsUrl, ct); return IcsParser.Parse(text); }
        catch (Exception ex) { _log.LogWarning(ex, "[Events] ICS fetch failed for league {Id}", l.Id); return new(); }
    }
}

/// <summary>Minimal RFC-5545 VEVENT parser (UID/SUMMARY/DTSTART/DTEND). Handles line folding, Z/UTC, and VALUE=DATE.</summary>
public static class IcsParser
{
    public static List<IngestedEvent> Parse(string ics)
    {
        var result = new List<IngestedEvent>();
        var lines = Unfold(ics);
        string? uid = null, summary = null; long? start = null, end = null; var dateOnly = false; var inEvent = false;

        foreach (var raw in lines)
        {
            var line = raw;
            if (line == "BEGIN:VEVENT") { inEvent = true; uid = summary = null; start = end = null; dateOnly = false; continue; }
            if (line == "END:VEVENT")
            {
                if (inEvent && start is { } s)
                    // Time-independent key when UID is missing (a moved event must update in place, not duplicate).
                    result.Add(new IngestedEvent(uid ?? $"nouid:{summary?.ToLowerInvariant().Trim() ?? "event"}", summary ?? "Event", s, end, dateOnly, null));
                inEvent = false;
                continue;
            }
            if (!inEvent) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon];
            var value = line[(colon + 1)..];
            var prop = name.Split(';')[0].ToUpperInvariant();

            if (prop == "UID") uid = value.Trim();
            else if (prop == "SUMMARY") summary = Unescape(value);
            else if (prop == "DTSTART") { var (e, d0) = ParseIcsTime(name, value); start = e; dateOnly = d0; }
            else if (prop == "DTEND") { var (e, _) = ParseIcsTime(name, value); end = e; }
        }
        return result;
    }

    private static (long? epoch, bool dateOnly) ParseIcsTime(string name, string value)
    {
        value = value.Trim();
        try
        {
            if (name.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && value.Length == 8)
            {
                var d = DateTime.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture);
                return (EpochTime.DisplayMidnightUtc(d.Year, d.Month, d.Day), true); // all-day → display-zone midnight
            }
            if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                var dt = DateTime.ParseExact(value.TrimEnd('Z', 'z'), "yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
                return (new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds(), false);
            }
            // A no-Z, non-DATE time is either floating local or TZID-qualified (e.g. DTSTART;TZID=Australia/Brisbane:...).
            // We don't parse TZID; interpret it as the configured display zone rather than UTC (the old TimeSpan.Zero
            // assumption shifted such events by the whole zone offset). (ICS is a legacy/dormant path.)
            if (DateTime.TryParseExact(value, "yyyyMMddTHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var n))
                return (EpochTime.DisplayWallClockToUtc(n), false);
        }
        catch { }
        return (null, false);
    }

    private static List<string> Unfold(string ics)
    {
        var outLines = new List<string>();
        foreach (var line in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && outLines.Count > 0)
                outLines[^1] += line.TrimStart();
            else
                outLines.Add(line);
        }
        return outLines;
    }

    private static string Unescape(string v) => v.Replace("\\,", ",").Replace("\\;", ";").Replace("\\n", " ").Replace("\\\\", "\\").Trim();
}
