using System.Text.RegularExpressions;
using DVarr.Data;
using DVarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

public sealed record ResolvedChannel(int ChannelId, int SourceId, int StreamId, string ChannelName, double Score, int Rank, double EpgScore = 0);
public sealed record ResolveResult(bool Ok, ResolvedChannel? Primary, List<ResolvedChannel> Fallbacks, double Confidence, string? Reason);

/// <summary>
/// Picks the channel for an event (docs/06 §5). A PINNED league→channel mapping carries a dominant
/// PIN_FLOOR that EPG data can never outrank (fixes the bug #2 hijack); EPG is only a bounded bonus on
/// an already-mapped channel; there is NO global confidence gate. Fallbacks are restricted to the SAME
/// credential as the winner (bug #7 — never steal another login's only slot).
/// </summary>
public sealed class ResolverService
{
    private const double PinFloor = 1000;
    private const double EpgBonusMax = 30;

    private readonly DVarrDbContext _db;
    private readonly ILogger<ResolverService> _log;

    public ResolverService(DVarrDbContext db, ILogger<ResolverService> log) { _db = db; _log = log; }

    /// <param name="restrictSourceId">When set, only channels on THIS credential are considered — used by the
    /// arm-window EPG re-pick, which must never move a recording across credentials (slot planning owns that).</param>
    public async Task<ResolveResult> ResolveAsync(int eventId, CancellationToken ct = default, int? restrictSourceId = null)
    {
        var ev = await _db.Events.FindAsync(new object?[] { eventId }, ct);
        if (ev is null) return new ResolveResult(false, null, new(), 0, "event not found");

        var maps = await _db.LeagueChannelMaps.Where(m => m.LeagueId == ev.LeagueId).OrderBy(m => m.Rank).ToListAsync(ct);
        if (maps.Count == 0) return new ResolveResult(false, null, new(), 0, "no channel mapping for this league");

        // Off-limits guard: never resolve to a channel on a disabled source, so the auto-scheduler can't even
        // create a Pending recording that would later try to contact it.
        var disabledSources = (await _db.Sources.Where(s => !s.Enabled).Select(s => s.Id).ToListAsync(ct)).ToHashSet();

        var scored = new List<ResolvedChannel>();
        foreach (var m in maps)
        {
            var ch = await _db.Channels.FindAsync(new object?[] { m.ChannelId }, ct);
            if (ch is null || !ch.Enabled || disabledSources.Contains(ch.SourceId)) continue;
            if (restrictSourceId is { } rs && ch.SourceId != rs) continue;

            // Pinned mappings dominate; ranked mappings score below them. EPG can only ADD a bounded bonus.
            var score = m.Pinned ? PinFloor - m.Rank : Math.Max(0, 100 - (m.Rank - 1) * 5);
            double epgSim = 0;

            // For a date-only event we only know the day (anchored to Brisbane midnight) — match across the whole
            // local day; for a timed event keep the tight 30-min window. Pick the BEST-matching programme, not the
            // first arbitrary overlap, so a multi-programme day still resolves to the right title.
            // EPG now lives in a per-source, tvg-id-keyed table (decoupled from the channel row), so join by
            // the channel's EpgChannelId on the same credential. No EpgChannelId → no EPG bonus (pin still wins).
            var eid = !string.IsNullOrEmpty(ch.EpgChannelId) ? ch.EpgChannelId : ch.MatchedEpgId; // provider id, else name-matched
            if (!string.IsNullOrEmpty(eid))
            {
                var winEnd = ev.StartUtc + (ev.StartIsDateOnly ? 86400 : 1800);
                // Programme.EpgChannelId is COLLATE NOCASE → this == is case-insensitive AND uses the (SourceId,EpgChannelId,StartUtc) index.
                var progTitles = await _db.Programmes
                    .Where(p => p.SourceId == ch.SourceId && p.EpgChannelId == eid && p.StartUtc <= winEnd && p.StopUtc >= ev.StartUtc)
                    .Select(p => p.Title).Take(50).ToListAsync(ct);
                if (progTitles.Count > 0)
                {
                    epgSim = progTitles.Max(t => Similarity(t, ev.Title));
                    score += epgSim * EpgBonusMax;
                }
            }

            scored.Add(new ResolvedChannel(ch.Id, ch.SourceId, ch.StreamId, ch.Name, score, m.Rank, epgSim));
        }
        if (scored.Count == 0) return new ResolveResult(false, null, new(), 0, "mapped channels are missing or disabled");

        // Highest score wins the primary; ties (e.g. two unpinned channels with no EPG bonus) break by the
        // user's rank so the fallback ladder is deterministic and matches the order shown on the Leagues page.
        scored = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Rank).ToList();
        var win = scored[0];
        // Same-credential fallbacks only (never cross-source — that would steal another login's slot).
        var fallbacks = scored.Skip(1).Where(x => x.SourceId == win.SourceId).ToList();
        return new ResolveResult(true, win, fallbacks, win.Score, null);
    }

    /// <summary>
    /// The SAME logical channel as <paramref name="channelId"/> but on the OTHER enabled credentials, for
    /// cross-login spreading (docs/06 conflict planning). Matched by StreamId (the two provider logins carry the
    /// same stream ids), then LogicalKey, then normalised name — best match first, one row per credential.
    /// Disabled sources (e.g. the off-limits Source 1) are never returned. Score/Rank are 0 (placement, not scoring).
    /// </summary>
    public async Task<List<ResolvedChannel>> EquivalentChannelsAsync(int channelId, CancellationToken ct = default)
    {
        var ch = await _db.Channels.FindAsync(new object?[] { channelId }, ct);
        if (ch is null) return new();
        var disabled = (await _db.Sources.Where(s => !s.Enabled).Select(s => s.Id).ToListAsync(ct)).ToHashSet();

        // An empty normalised key/name must NOT act as a wildcard (junk channels like "||" or "UK:" normalise to ""),
        // or an empty-named target would cross-match arbitrary other-source channels and re-home a recording to the
        // wrong channel. Only match LogicalKey/NameNorm when they're non-empty; StreamId equality always stands.
        var hasKey = !string.IsNullOrEmpty(ch.LogicalKey);
        var hasName = !string.IsNullOrEmpty(ch.NameNorm);
        var rows = await _db.Channels
            .Where(c => c.Enabled && c.SourceId != ch.SourceId &&
                        (c.StreamId == ch.StreamId
                         || (hasKey && c.LogicalKey == ch.LogicalKey)
                         || (hasName && c.NameNorm == ch.NameNorm)))
            .ToListAsync(ct);

        int Quality(Channel c) => c.StreamId == ch.StreamId ? 3 : (hasKey && c.LogicalKey == ch.LogicalKey ? 2 : 1);

        return rows.Where(c => !disabled.Contains(c.SourceId))
            .GroupBy(c => c.SourceId)
            .Select(g => g.OrderByDescending(Quality).First()) // best-matching row per credential
            .Select(c => new ResolvedChannel(c.Id, c.SourceId, c.StreamId, c.Name, 0, 0))
            .ToList();
    }

    private static readonly Regex NonAlnum = new(@"[^a-z0-9 ]", RegexOptions.Compiled);

    /// <summary>Token-set Jaccard similarity 0..1 on normalised titles (lightweight fuzzy match).</summary>
    internal static double Similarity(string? a, string? b)
    {
        var ta = Tokens(a); var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Intersect(tb).Count();
        var union = ta.Union(tb).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    private static HashSet<string> Tokens(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new();
        var n = NonAlnum.Replace(s.ToLowerInvariant(), " ");
        return n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet();
    }
}
