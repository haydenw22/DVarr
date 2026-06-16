using System.Globalization;
using System.Xml;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Ingest;

public sealed record EpgResult(int SourceId, bool Ok, int Programmes, int ChannelsMatched, bool Truncated, string? Error);

/// <summary>
/// Parses an XMLTV EPG (the provider's xmltv.php, or a per-source external override URL) and stores
/// the ENTIRE guide for that source: every XMLTV channel is kept (keyed by epg_channel_id), not just
/// channels already in the lineup, so the guide is complete and order-independent (the channel list and
/// the EPG can be ingested in any order). Bounded only by a generous, configurable time window and a
/// high safety cap. Inserts stream in bounded batches so a 100MB+ EPG never buffers in memory or holds
/// the write gate. Contacts the provider/external URL, so it only runs on explicit request.
/// </summary>
public sealed class EpgIngestService
{
    private const int BatchSize = 5000;

    private readonly DVarrDbContext _db;
    private readonly XtreamClient _xtream;
    private readonly DbWriteGate _gate;
    private readonly SettingsService _settings;
    private readonly ILogger<EpgIngestService> _log;

    public EpgIngestService(DVarrDbContext db, XtreamClient xtream, DbWriteGate gate, SettingsService settings, ILogger<EpgIngestService> log)
    {
        _db = db; _xtream = xtream; _gate = gate; _settings = settings; _log = log;
    }

    public async Task<EpgResult> SyncSourceEpgAsync(int sourceId, CancellationToken ct = default)
    {
        var s = await _db.Sources.FindAsync(new object?[] { sourceId }, ct);
        if (s is null) return new EpgResult(sourceId, false, 0, 0, false, "source not found");
        // A disabled source is off-limits — but only block the PROVIDER's own xmltv.php. An external EPG override URL
        // (a separate XMLTV provider) is independent of the provider login, so allow it even when the source is disabled.
        if (!s.Enabled && !(s.EpgOverride && !string.IsNullOrWhiteSpace(s.EpgUrl)))
            return new EpgResult(sourceId, false, 0, 0, false, "source is disabled — refusing to contact the provider EPG");

        var now = EpochTime.Now();
        var pastH = await _settings.GetIntAsync("epg_past_window_h"); if (pastH <= 0) pastH = 48;
        var futureD = await _settings.GetIntAsync("epg_future_window_d"); if (futureD <= 0) futureD = 21;
        var cap = await _settings.GetIntAsync("epg_max_programmes"); if (cap <= 0) cap = 3_000_000;
        long winStart = now - (long)pastH * 3600, winEnd = now + (long)futureD * 86400;

        // Replace this source's programmes in one short delete (a guide refresh never affects a recording —
        // recordings anchor to Event.start_utc, not programme ids; docs/06 §5.6), then stream inserts in
        // bounded batches each taking the gate only briefly, so a recording start/finalize never stalls.
        await _gate.WriteAsync(async () =>
        {
            await _db.Programmes.Where(p => p.SourceId == sourceId).ExecuteDeleteAsync(ct);
        }, ct);

        var total = 0;
        var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var batch = new List<Programme>(BatchSize);
        var nameIndex = new Dictionary<string, string>(StringComparer.Ordinal); // normalised EPG <display-name> → tvg-id (for name matching)
        var ambiguousNames = new HashSet<string>(StringComparer.Ordinal);       // norms that map to >1 different tvg-id → don't guess

        async Task FlushBatchAsync()
        {
            if (batch.Count == 0) return;
            var toWrite = batch;
            batch = new List<Programme>(BatchSize);
            await _gate.WriteAsync(async () =>
            {
                _db.Programmes.AddRange(toWrite);
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }, ct);
        }

        try
        {
            await using var stream = await _xtream.OpenEpgAsync(s, ct);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreWhitespace = true });

            string? curCh = null; long? curStart = null, curStop = null; string curTitle = ""; var inProg = false;
            void Complete()
            {
                if (inProg && !string.IsNullOrEmpty(curCh) && curStart is { } st)
                {
                    var stop = curStop ?? st;
                    if (stop >= winStart && st <= winEnd)
                    {
                        channels.Add(curCh!);
                        batch.Add(new Programme
                        {
                            SourceId = sourceId, EpgChannelId = curCh!, StartUtc = st, StopUtc = stop,
                            Title = curTitle.Length == 0 ? "(no title)" : curTitle,
                            EpgUid = $"{curCh}:{st}",
                        });
                        total++;
                    }
                }
                inProg = false; curCh = null; curStart = curStop = null; curTitle = "";
            }

            string? curChId = null; var inChannel = false; // XMLTV lists <channel> elements (with <display-name>s) before <programme>s
            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "channel")
                {
                    inChannel = true; curChId = reader.GetAttribute("id");
                    if (reader.IsEmptyElement) { inChannel = false; curChId = null; }
                }
                else if (inChannel && reader.NodeType == XmlNodeType.Element && reader.Name == "display-name")
                {
                    // Index each display-name (incl. the provider-format one many EPGs carry) → tvg-id, for name matching.
                    // If two DIFFERENT channels normalise to the same name, mark it ambiguous so we never guess wrong.
                    if (!string.IsNullOrEmpty(curChId))
                        try
                        {
                            var dn = MatchNorm(await reader.ReadElementContentAsStringAsync());
                            if (dn.Length > 0)
                            {
                                if (nameIndex.TryGetValue(dn, out var existing)) { if (!string.Equals(existing, curChId, StringComparison.OrdinalIgnoreCase)) ambiguousNames.Add(dn); }
                                else nameIndex[dn] = curChId!;
                            }
                        }
                        catch { }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "channel")
                {
                    inChannel = false; curChId = null;
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
                {
                    if (inProg) Complete();
                    inProg = true;
                    curCh = reader.GetAttribute("channel");
                    curStart = ParseXmltvTime(reader.GetAttribute("start"));
                    curStop = ParseXmltvTime(reader.GetAttribute("stop"));
                    curTitle = "";
                    if (reader.IsEmptyElement) Complete();
                }
                else if (inProg && reader.NodeType == XmlNodeType.Element && reader.Name == "title")
                {
                    // A single malformed title must not abort the whole source's EPG.
                    if (curTitle.Length == 0) { try { curTitle = (await reader.ReadElementContentAsStringAsync()).Trim(); } catch { } }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "programme")
                {
                    Complete();
                }

                if (total >= cap) { truncated = true; break; }       // very high safety guard against a runaway feed
                if (batch.Count >= BatchSize) await FlushBatchAsync(); // bound memory: never accumulate the whole EPG
            }
            Complete();             // flush a trailing <programme> whose <title> was the final child read
            await FlushBatchAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[EPG] Source {Id} fetch/parse failed", sourceId);
            await FlushBatchAsync(); // persist whatever parsed before the error so a partial guide survives
            return new EpgResult(sourceId, false, total, channels.Count, truncated, "EPG fetch/parse failed: " + ex.Message);
        }

        // Name-match channels the provider left without an epg_channel_id (TiviMate-style), so the guide fills in
        // for almost every channel rather than only the ~15% the provider tagged.
        await BackfillMatchedEpgIdsAsync(sourceId, nameIndex, ambiguousNames, ct);

        _log.LogInformation("[EPG] Source {Id} ({Label}): {P} programmes across {C} EPG channels ({Mode}){Trunc}",
            sourceId, s.Label, total, channels.Count,
            s.EpgOverride && !string.IsNullOrWhiteSpace(s.EpgUrl) ? "external" : "provider",
            truncated ? " [TRUNCATED at safety cap]" : "");
        return new EpgResult(sourceId, true, total, channels.Count, truncated, null);
    }

    private static readonly HashSet<string> QualityTokens = new(StringComparer.Ordinal)
    { "hd", "fhd", "uhd", "sd", "fullhd", "4k", "8k", "raw", "hevc", "h265", "h264", "fps", "hq", "lq", "vip" };

    /// <summary>Normalise a channel name for matching: lowercase, strip to ASCII alphanumerics (drops the ## / superscript
    /// decorations IPTV names love), and remove quality tokens (HD/4K/RAW/…) so "AU: FOX SPORTS 503 HD" ≈ the EPG's name.</summary>
    private static string MatchNorm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.ToLowerInvariant().Select(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !QualityTokens.Contains(t)));
    }

    /// <summary>For channels with no provider epg_channel_id, set MatchedEpgId by normalised-name lookup into the EPG's
    /// channel list. Only writes the rows whose match actually changed, in small batches off the write gate.</summary>
    private async Task BackfillMatchedEpgIdsAsync(int sourceId, Dictionary<string, string> nameIndex, HashSet<string> ambiguousNames, CancellationToken ct)
    {
        if (nameIndex.Count == 0) return;
        // Loads the source's untagged channels (tens of thousands of tiny {Id,Name} rows — a few MB, one-shot;
        // the only non-batched read here, bounded by channel count). Writes only the rows whose match changed.
        var chans = await _db.Channels
            .Where(c => c.SourceId == sourceId && (c.EpgChannelId == null || c.EpgChannelId == ""))
            .Select(c => new { c.Id, c.Name, c.MatchedEpgId }).ToListAsync(ct);

        var changed = new List<(int Id, string? Matched)>();
        foreach (var c in chans)
        {
            var norm = MatchNorm(c.Name);
            // Skip ambiguous norms (a name shared by >1 EPG channel) — better no guide than the wrong one.
            string? matched = norm.Length > 0 && !ambiguousNames.Contains(norm) && nameIndex.TryGetValue(norm, out var tv) ? tv : null;
            if (!string.Equals(matched, c.MatchedEpgId, StringComparison.OrdinalIgnoreCase)) changed.Add((c.Id, matched));
        }

        for (var i = 0; i < changed.Count; i += 2000)
        {
            var slice = changed.GetRange(i, Math.Min(2000, changed.Count - i));
            var map = slice.ToDictionary(x => x.Id, x => x.Matched);
            var ids = slice.Select(x => x.Id).ToList();
            await _gate.WriteAsync(async () =>
            {
                var entities = await _db.Channels.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
                foreach (var e in entities) e.MatchedEpgId = map[e.Id];
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }, ct);
        }
        _log.LogInformation("[EPG] Source {Id}: name-matched {N} channels to the EPG (no provider tvg-id)", sourceId, changed.Count(x => x.Matched != null));
    }

    /// <summary>Parse XMLTV time "yyyyMMddHHmmss [+/-HHMM]" to a UTC epoch.</summary>
    internal static long? ParseXmltvTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 14) return null;
        try
        {
            var dt = DateTime.ParseExact(s.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
            var off = TimeSpan.Zero;
            var rest = s.Length > 14 ? s.Substring(14).Trim() : "";
            if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-'))
            {
                var sign = rest[0] == '-' ? -1 : 1;
                off = new TimeSpan(sign * int.Parse(rest.Substring(1, 2)), sign * int.Parse(rest.Substring(3, 2)), 0);
            }
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), off).ToUnixTimeSeconds();
        }
        catch { return null; }
    }
}
