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
    // One lock per source so two concurrent syncs of the SAME source can't race the last-known-good swap (the
    // maxOldId threshold is read outside the gate). Static because the service is scoped per request; different
    // sources still sync in parallel.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _sourceLocks = new();

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
        // Serialize per source: a second concurrent sync of the same source fast-fails rather than racing the swap.
        var gate = _sourceLocks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            return new EpgResult(sourceId, false, 0, 0, false, "an EPG sync is already in progress for this source");
        try { return await SyncSourceEpgCoreAsync(sourceId, ct); }
        finally { gate.Release(); }
    }

    private async Task<EpgResult> SyncSourceEpgCoreAsync(int sourceId, CancellationToken ct = default)
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

        // LAST-KNOWN-GOOD swap: capture the highest existing programme Id for this source, stream the NEW rows in
        // (their Ids are strictly above maxOldId), and only AFTER a fully successful parse delete the OLD rows
        // (Id <= maxOldId). On ANY failure we instead delete the partial NEW rows, leaving the previous guide intact —
        // a failed sync can never wipe the guide. A guide refresh never affects a recording (recordings anchor to
        // Event.start_utc, not programme ids; docs/06 §5.6). Old+new briefly coexist mid-sync (a re-syncable cache),
        // and inserts still stream in bounded batches each taking the gate only briefly so a recording never stalls.
        var maxOldId = await _db.Programmes.Where(p => p.SourceId == sourceId).Select(p => (int?)p.Id).MaxAsync(ct) ?? 0;
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
                    var stop = (curStop is { } cs && cs > st) ? cs : st + 3600; // missing/back-dated stop → assume 1h so every row has a forward interval
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
            if (!truncated)
                // Full parse succeeded → swap: drop the OLD guide, leaving only this run's rows. Stamp the source's
                // last-good EPG sync time in the same write — the re-pick sweep reads it to detect a >12h-stale guide.
                await _gate.WriteAsync(async () =>
                {
                    await _db.Programmes.Where(p => p.SourceId == sourceId && p.Id <= maxOldId).ExecuteDeleteAsync(ct);
                    // Re-load the row inside the gate — the batch flushes above call ChangeTracker.Clear(), so the `s`
                    // captured before streaming may be detached. FindAsync returns the tracked (or freshly-read) entity.
                    var src = await _db.Sources.FindAsync(new object?[] { sourceId }, ct);
                    if (src is not null) { src.LastEpgSyncUtc = EpochTime.Now(); await _db.SaveChangesAsync(ct); }
                }, ct);
            else
                // Hit the safety cap mid-stream → this run's guide is INCOMPLETE. Keep the last-known-good guide and
                // discard this run's partial rows, rather than overwriting a complete guide with a truncated one.
                await _gate.WriteAsync(async () => { await _db.Programmes.Where(p => p.SourceId == sourceId && p.Id > maxOldId).ExecuteDeleteAsync(ct); }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[EPG] Source {Id} fetch/parse failed — keeping the previous guide", sourceId);
            // Discard this run's partial new rows; the OLD guide (Id <= maxOldId) is left untouched. Best-effort.
            try { await _gate.WriteAsync(async () => { await _db.Programmes.Where(p => p.SourceId == sourceId && p.Id > maxOldId).ExecuteDeleteAsync(ct); }, ct); } catch { }
            return new EpgResult(sourceId, false, total, channels.Count, truncated, "EPG fetch/parse failed: " + ex.Message);
        }

        // Name-match channels the provider left without an epg_channel_id (TiviMate-style), so the guide fills in
        // for almost every channel rather than only the ~15% the provider tagged.
        await BackfillMatchedEpgIdsAsync(sourceId, nameIndex, ambiguousNames, ct);

        _log.LogInformation("[EPG] Source {Id} ({Label}): {P} programmes across {C} EPG channels ({Mode}){Trunc}",
            sourceId, s.Label, total, channels.Count,
            s.EpgOverride && !string.IsNullOrWhiteSpace(s.EpgUrl) ? "external" : "provider",
            truncated ? " [TRUNCATED at safety cap]" : "");
        // A truncated run committed NOTHING (its partial rows were discarded above) — reporting Ok=true with the
        // attempted count told the user a sync succeeded that actually kept the old guide (audit EPG-01). Say so.
        if (truncated)
            return new EpgResult(sourceId, false, total, channels.Count, true,
                $"guide exceeded the programme safety cap (epg_max_programmes) at {total:N0} rows — this run was discarded and the previous guide kept; raise the cap or point this source at a smaller external EPG");
        return new EpgResult(sourceId, true, total, channels.Count, false, null);
    }

    private static readonly HashSet<string> QualityTokens = new(StringComparer.Ordinal)
    { "hd", "fhd", "uhd", "sd", "fullhd", "4k", "8k", "raw", "hevc", "h265", "h264", "fps", "hq", "lq", "vip" };

    // Region/country tokens dropped when collapsing a name to its "core" (see Core). Kept TIGHT — only unambiguous
    // country/region codes, never a token that could be real content (e.g. NOT "tv"). Compared against MatchNorm's
    // already-lowercased tokens; OrdinalIgnoreCase is belt-and-suspenders.
    private static readonly HashSet<string> RegionTokens = new(StringComparer.OrdinalIgnoreCase)
    { "au", "aus", "us", "usa", "uk", "gb", "nz", "ca", "ie", "za", "in", "sg", "ph", "my" };

    /// <summary>Normalise a channel name for matching: lowercase, strip to ASCII alphanumerics (drops the ## / superscript
    /// decorations IPTV names love), and remove quality tokens (HD/4K/RAW/…) so "AU: FOX SPORTS 503 HD" ≈ the EPG's name.</summary>
    private static string MatchNorm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.ToLowerInvariant().Select(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !QualityTokens.Contains(t)));
    }

    /// <summary>Collapsed-core id bridge: MatchNorm the string, DROP region/country tokens, then CONCATENATE the
    /// remaining tokens with no spaces — bridging a provider's concatenated programme id to a spaced channel name.
    /// Core("AU: FOX SPORTS 503 HD") == Core("foxsports503.au") == "foxsports503"; Core("US: FOX SPORTS 1 RAW") ==
    /// Core("foxsports1.us") == "foxsports1". Heals channels whose provider tvg-id is wrong/empty but whose EPG keys
    /// programmes under a concatenated id that ships no matching &lt;channel&gt; definition.</summary>
    private static string Core(string? s)
    {
        var norm = MatchNorm(s);
        if (norm.Length == 0) return "";
        return string.Concat(norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !RegionTokens.Contains(t)));
    }

    /// <summary>Self-heals the channel↔guide join for a source. For channels whose CURRENT EFFECTIVE id
    /// (EpgChannelId ?? MatchedEpgId) has no guide, sets MatchedEpgId by (1) normalised-name lookup into the EPG's
    /// &lt;channel&gt; list, then (2) a collapsed-core bridge against the ids that actually carry programmes. Both indexes
    /// only hold ids that have programmes, so a match is always live. ADDITIONALLY clears a non-empty-but-DEAD provider
    /// EpgChannelId (only once a live replacement match exists) so the unchanged Eff = EpgChannelId ?? MatchedEpgId at
    /// every read site falls through to the new MatchedEpgId. Writes only the rows that change, in small batches off the
    /// gate. Idempotent: a healed channel resolves live next run and is skipped, so working channels never change.</summary>
    private async Task BackfillMatchedEpgIdsAsync(int sourceId, Dictionary<string, string> nameIndex, HashSet<string> ambiguousNames, CancellationToken ct)
    {
        // The ids that ACTUALLY carry programmes for this source (a few thousand distinct rows — one query). A cheap EPG
        // may key programmes under ids it never <channel>-defines (e.g. "foxsports503.au"), and the provider's
        // get_live_streams may hand a channel a WRONG or empty tvg-id — so this, not the <channel> name list, is the
        // source of truth for "does this id resolve to real guide rows".
        var progIds = await _db.Programmes
            .Where(p => p.SourceId == sourceId && p.EpgChannelId != null && p.EpgChannelId != "")
            .Select(p => p.EpgChannelId!).Distinct().ToListAsync(ct);

        // Every id that has a guide (case-insensitive — Programme.EpgChannelId is COLLATE NOCASE, as the read sites join).
        var hasProg = new HashSet<string>(progIds, StringComparer.OrdinalIgnoreCase);

        // coreIndex: Core(id) → id over the ids that have programmes, so a channel whose provider id is wrong/dead can be
        // bridged to a LIVE programme id by its collapsed core ("foxsports503"). Same ambiguity discipline as nameIndex:
        // if two DIFFERENT ids collapse to one core, poison that core and never match it. Cores under MinCoreLen are too
        // generic to trust. By construction every value here is in hasProg → a core match is ALWAYS live.
        const int MinCoreLen = 5;
        var coreIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        var coreAmbiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in progIds)
        {
            var core = Core(id);
            if (core.Length < MinCoreLen || coreAmbiguous.Contains(core)) continue;
            if (coreIndex.TryGetValue(core, out var existing))
            {
                if (!string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)) { coreAmbiguous.Add(core); coreIndex.Remove(core); }
            }
            else coreIndex[core] = id;
        }

        // Nothing to match against at all (no <channel> names AND no programme ids) → nothing to do.
        if (nameIndex.Count == 0 && coreIndex.Count == 0) return;

        // Load every channel of the source (tens of thousands of tiny rows — the method's accepted one-shot cost) and
        // filter to CANDIDATES in memory (EF-translating a big NOT IN is bad). A candidate is a channel whose current
        // effective id has NO guide; a channel already resolving to a live id is skipped — which is exactly why a channel
        // with a LIVE provider EpgChannelId is never touched, and why the run is idempotent (a healed channel resolves
        // live and is skipped next time).
        var chans = await _db.Channels
            .Where(c => c.SourceId == sourceId)
            .Select(c => new { c.Id, c.Name, c.EpgChannelId, c.MatchedEpgId }).ToListAsync(ct);

        // (Id, value to write to MatchedEpgId or null to leave it, whether to clear the dead provider EpgChannelId).
        var changed = new List<(int Id, string? SetMatched, bool ClearProviderId)>();
        foreach (var c in chans)
        {
            // Effective id today = EpgChannelId if non-empty, else MatchedEpgId — the exact rule the 4 read sites apply.
            var effectiveId = !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId;
            // Already resolves to a live guide → leave it COMPLETELY alone. This single guard is what makes any working
            // channel (live provider id OR already-name-matched) a non-candidate: zero writes, MatchedEpgId untouched.
            if (!string.IsNullOrEmpty(effectiveId) && hasProg.Contains(effectiveId!)) continue;

            // Match: display-name path FIRST (unchanged behaviour) so nothing about current successful matching changes;
            // only if that misses do we try the collapsed-core bridge.
            var norm = MatchNorm(c.Name);
            string? matched = norm.Length > 0 && !ambiguousNames.Contains(norm) && nameIndex.TryGetValue(norm, out var tv) ? tv : null;
            if (matched is null)
            {
                var core = Core(c.Name);
                if (core.Length >= MinCoreLen && !coreAmbiguous.Contains(core) && coreIndex.TryGetValue(core, out var cv)) matched = cv;
            }
            if (matched is null) continue; // no match → never downgrade an existing MatchedEpgId to null

            // A non-empty provider id that is confirmed DEAD is cleared so Eff falls through to MatchedEpgId — but ONLY
            // when the replacement is confirmed LIVE (always true for a core match; also guards a degenerate empty-guide
            // sync). Never clears a live provider id.
            var clearProviderId = !string.IsNullOrEmpty(c.EpgChannelId) && !hasProg.Contains(c.EpgChannelId!) && hasProg.Contains(matched);
            // Only a positive, CHANGED match writes MatchedEpgId; never null out an existing one (LKG protection).
            var matchChanged = !string.Equals(matched, c.MatchedEpgId, StringComparison.OrdinalIgnoreCase);
            if (matchChanged || clearProviderId) changed.Add((c.Id, matchChanged ? matched : null, clearProviderId));
        }

        for (var i = 0; i < changed.Count; i += 2000)
        {
            var slice = changed.GetRange(i, Math.Min(2000, changed.Count - i));
            var map = slice.ToDictionary(x => x.Id, x => (x.SetMatched, x.ClearProviderId));
            var ids = slice.Select(x => x.Id).ToList();
            await _gate.WriteAsync(async () =>
            {
                var entities = await _db.Channels.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
                foreach (var e in entities)
                {
                    var (setMatched, clear) = map[e.Id];
                    if (setMatched != null) e.MatchedEpgId = setMatched; // only positive matches; never null out an existing match
                    if (clear) e.EpgChannelId = null;                    // drop the confirmed-dead provider id (live replacement in hand)
                }
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }, ct);
        }
        _log.LogInformation("[EPG] Source {Id}: matched {N} channels to the EPG (name/core), cleared {D} dead provider tvg-ids",
            sourceId, changed.Count(x => x.SetMatched != null), changed.Count(x => x.ClearProviderId));
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
