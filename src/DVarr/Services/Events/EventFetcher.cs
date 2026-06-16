using System.Globalization;
using DVarr.Data.Entities;
using DVarr.Infrastructure;

namespace DVarr.Services.Events;

/// <summary>A normalised event. ExternalId is provider-stable (drives the churn-proof natural key). Carries
/// optional TheSportsDB enrichment (thumbnail, round, season) for Plex-clean media import + the Plex provider.</summary>
public sealed record IngestedEvent(string ExternalId, string Title, long StartUtc, long? EndUtc, bool DateOnly, string? Status,
    string? ThumbUrl = null, int? Round = null, string? Season = null);

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
        var byId = new Dictionary<string, IngestedEvent>(StringComparer.Ordinal); // dedupe by stable idEvent; first writer wins

        var seasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // season strings seen, for gap-fill scoping

        void Merge(IReadOnlyList<TsdbEvent> evs)
        {
            foreach (var e in evs)
            {
                if (e.StartUtc is not { } st) continue;
                if (!string.IsNullOrWhiteSpace(e.Season)) seasons.Add(e.Season!);
                // TheSportsDB never returns an end time — leave it null here; EventIngestService fills in the
                // per-sport default duration (2h soccer / 3h motorsport) so it's tunable in one place.
                byId.TryAdd(e.Id, new IngestedEvent(e.Id, e.Title, st, null, e.DateOnly, e.Status, e.Thumb ?? e.Poster, e.Round, e.Season));
            }
        }

        // 1) Season endpoint — richest metadata (round/season) but TheSportsDB's FREE tier caps it to a season's
        //    first few matches, so it's only a seed. It also anchors the competition start for the day-sweep below.
        var year = EpochTime.ToBrisbane(EpochTime.Now()).Year;
        foreach (var season in new[] { year.ToString(), $"{year}-{year + 1}", $"{year - 1}-{year}" })
        {
            var evs = await _tsdb.GetSeasonEventsAsync(leagueId, season, ct);
            if (evs.Count == 0) continue;
            Merge(evs);
            break; // first non-empty season wins (don't mix adjacent seasons' fixtures)
        }

        // 2) Per-day sweep from the competition START (earliest season event, else 45 days back) through the league's
        //    horizon. eventsday isn't capped like eventsseason — but it DROPS games unpredictably. Sweeping from the
        //    start (not just yesterday) is what makes the episode ordinal the true tournament game number instead of a
        //    position in a partial window. 429s degrade gracefully (that day yields nothing).
        var horizon = Math.Clamp(l.ScheduleHorizonDays, 1, 30);
        var nowUtc = DateTimeOffset.FromUnixTimeSeconds(EpochTime.Now()).UtcDateTime.Date;
        var earliest = byId.Values.Count > 0 ? byId.Values.Min(e => e.StartUtc) : (long?)null;
        var startDate = earliest is { } es ? DateTimeOffset.FromUnixTimeSeconds(es).UtcDateTime.Date.AddDays(-1) : nowUtc.AddDays(-45);
        var endDate = nowUtc.AddDays(horizon + 1);
        if ((endDate - startDate).TotalDays > 120) startDate = endDate.AddDays(-120); // safety cap on the sweep span
        for (var day = startDate; day <= endDate; day = day.AddDays(1))
        {
            try { Merge(await _tsdb.GetDayEventsAsync(leagueId, day.ToString("yyyy-MM-dd"), ct)); }
            catch (OperationCanceledException) { return byId.Values.ToList(); }
            catch (Exception ex) { _log.LogDebug(ex, "[Events] eventsday {Day:yyyy-MM-dd} failed for league {Id}", day, l.Id); }
            try { await Task.Delay(200, ct); } catch (OperationCanceledException) { return byId.Values.ToList(); }
        }

        // 3) Gap-fill via lookupevent. eventsday/eventsseason on the free tier silently omit some fixtures (confirmed:
        //    Australia v Turkey, Sweden v Tunisia, Netherlands v Japan), which would corrupt the chronological episode
        //    numbering. TheSportsDB numbers a competition's games near-sequentially by idEvent, so a SMALL gap between
        //    two observed ids is a dropped game — look each one up directly (the per-id endpoint hits the full DB).
        //    Bounded by gap size + a hard cap so a sparse league can't trigger a huge sweep, and the large gap between
        //    a competition's separate id blocks is never crossed.
        const int maxFillGap = 50, maxFills = 250;
        var ids = byId.Keys.Select(k => long.TryParse(k, out var n) ? n : -1L).Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
        var fills = 0;
        for (var i = 0; i + 1 < ids.Count && fills < maxFills; i++)
        {
            var gap = ids[i + 1] - ids[i];
            if (gap <= 1 || gap > maxFillGap) continue;
            for (var id = ids[i] + 1; id < ids[i + 1] && fills < maxFills; id++)
            {
                var key = id.ToString();
                if (byId.ContainsKey(key)) continue;
                fills++;
                try
                {
                    var ev = await _tsdb.GetEventByIdAsync(key, ct);
                    if (ev?.StartUtc is { } st && string.Equals(ev.LeagueId, leagueId, StringComparison.Ordinal)
                        && (string.IsNullOrWhiteSpace(ev.Season) || seasons.Count == 0 || seasons.Contains(ev.Season!)))
                        byId.TryAdd(ev.Id, new IngestedEvent(ev.Id, ev.Title, st, null, ev.DateOnly, ev.Status, ev.Thumb ?? ev.Poster, ev.Round, ev.Season));
                }
                catch (OperationCanceledException) { return byId.Values.ToList(); }
                catch (Exception ex) { _log.LogDebug(ex, "[Events] gap-fill lookupevent {Id} failed for league {Lid}", id, l.Id); }
                try { await Task.Delay(200, ct); } catch (OperationCanceledException) { return byId.Values.ToList(); }
            }
        }

        _log.LogInformation("[Events] TheSportsDB league {Id} ({Ext}): {Count} events (season + day-sweep {Start:yyyy-MM-dd}..{End:yyyy-MM-dd} + {Fills} gap-fill lookups)",
            l.Id, leagueId, byId.Count, startDate, endDate, fills);
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
                var bne = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, EpochTime.BrisbaneOffset);
                return (bne.ToUnixTimeSeconds(), true);
            }
            if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                var dt = DateTime.ParseExact(value.TrimEnd('Z', 'z'), "yyyyMMddTHHmmss", CultureInfo.InvariantCulture);
                return (new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds(), false);
            }
            // A no-Z, non-DATE time is either floating local or TZID-qualified (e.g. DTSTART;TZID=Australia/Brisbane:...).
            // We don't parse TZID, but this is a Brisbane-based deployment, so interpret it as Brisbane local rather
            // than UTC (the old TimeSpan.Zero assumption shifted such events by 10h). (ICS is a legacy/dormant path.)
            if (DateTime.TryParseExact(value, "yyyyMMddTHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var n))
                return (new DateTimeOffset(n, EpochTime.BrisbaneOffset).ToUnixTimeSeconds(), false);
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
