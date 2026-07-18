using System.Text.RegularExpressions;
using DVarr.Data;
using DVarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

public sealed record ResolvedChannel(int ChannelId, int SourceId, int StreamId, string ChannelName, double Score, int Rank, double EpgScore = 0, bool Pinned = false);
public sealed record ResolveResult(bool Ok, ResolvedChannel? Primary, List<ResolvedChannel> Fallbacks, double Confidence, string? Reason);

/// <summary>
/// Picks the channel for an event (docs/06 §5). A TEAM-scoped mapping (issue #5 — Yankees→YES, Mets→SNY
/// inside one league) applies only to that team's games and carries a dominant TEAM_FLOOR above every
/// league-wide mapping. Below that, a PINNED mapping carries a dominant PIN_FLOOR that EPG data can never
/// outrank (fixes the bug #2 hijack); EPG is only a bounded bonus on an already-mapped channel; there is
/// NO global confidence gate. Fallbacks are restricted to the SAME credential as the winner (bug #7 —
/// never steal another login's only slot).
/// </summary>
public sealed class ResolverService
{
    private const double PinFloor = 1000;
    private const double EpgBonusMax = 30;
    // A team-scoped mapping matching one of the event's teams dominates every league-wide mapping (pinned or not),
    // while pin/rank/EPG keep their existing order WITHIN the team-scoped set. Far above PinFloor + EpgBonusMax.
    private const double TeamFloor = 10000;
    // A single-team / generic EPG match (a team-branded channel that mentions the team but not the opponent) earns
    // at most this many points — below the 5-point rank step, so it can never leapfrog the user's channel order.
    private const double WeakEpgCap = 3;

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

        // Team scope: a mapping with a TeamId applies ONLY to that team's games. Drop non-matching team-scoped rows
        // (a Mets game must never land on the Yankees' channel); an event with no team ids (motorsport / missing
        // data) keeps only the league-wide rows. League-wide mappings always stay as candidates/fallbacks.
        bool TeamMatch(LeagueChannelMap m) => m.TeamId is { Length: > 0 } t && (t == ev.HomeTeamId || t == ev.AwayTeamId);
        maps = maps.Where(m => string.IsNullOrEmpty(m.TeamId) || TeamMatch(m)).ToList();
        if (maps.Count == 0) return new ResolveResult(false, null, new(), 0, "no channel mapping applies to this event (all mappings are scoped to other teams)");

        // Off-limits guard: never resolve to a channel on a disabled source, so the auto-scheduler can't even
        // create a Pending recording that would later try to contact it.
        var disabledSources = (await _db.Sources.Where(s => !s.Enabled).Select(s => s.Id).ToListAsync(ct)).ToHashSet();

        // The two teams of this event (for the "does the guide show THIS game, not just this team" test below).
        var (sideA, sideB) = EventSides(ev.Title);

        var scored = new List<ResolvedChannel>();
        foreach (var m in maps)
        {
            var ch = await _db.Channels.FindAsync(new object?[] { m.ChannelId }, ct);
            if (ch is null || !ch.Enabled || disabledSources.Contains(ch.SourceId)) continue;
            if (restrictSourceId is { } rs && ch.SourceId != rs) continue;

            // Team-scoped mappings dominate for their team's games; below that, pinned mappings dominate ranked
            // ones. EPG can only ADD a bounded bonus.
            var score = m.Pinned ? PinFloor - m.Rank : Math.Max(0, 100 - (m.Rank - 1) * 5);
            if (TeamMatch(m)) score += TeamFloor;
            double epgSim = 0;

            // For a date-only event we only know the day (anchored to display-zone midnight) — match across the whole
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
                    // Distinguish a programme that shows THIS game (BOTH teams) from one that merely mentions the
                    // followed team: a team-branded channel ("US: New York Mets") matches every Mets game on the team
                    // name alone (~0.4) and used to hijack the pick from the real regional network when that network
                    // wasn't carrying the game. Only a both-team match earns the rank-overriding bonus AND a non-zero
                    // EpgScore (which the arm-window re-pick keys off); a single-team/generic match earns at most
                    // WeakEpgCap — below the rank step, so it can't reorder the ladder.
                    double strong = 0, weak = 0;
                    foreach (var t in progTitles)
                    {
                        var sim = Similarity(t, ev.Title);
                        if (ShowsBothTeams(t, sideA, sideB)) strong = Math.Max(strong, sim);
                        else weak = Math.Max(weak, sim);
                    }
                    epgSim = strong; // only a real-game (both-team) match counts as "this channel shows the event"
                    score += strong > 0 ? strong * EpgBonusMax : Math.Min(weak * EpgBonusMax, WeakEpgCap);
                }
            }

            scored.Add(new ResolvedChannel(ch.Id, ch.SourceId, ch.StreamId, ch.Name, score, m.Rank, epgSim, m.Pinned));
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
    /// cross-login spreading (docs/06 conflict planning). Matched by StreamId — but ONLY between credentials of
    /// the SAME provider (same normalised host: numeric stream ids are catalogue-local, and unrelated providers
    /// reuse the same small integers, audit RES-01) — then LogicalKey, then normalised name. Best match first,
    /// one row per credential. Disabled sources are never returned. Score/Rank are 0 (placement, not scoring).
    /// </summary>
    public async Task<List<ResolvedChannel>> EquivalentChannelsAsync(int channelId, CancellationToken ct = default)
    {
        var ch = await _db.Channels.FindAsync(new object?[] { channelId }, ct);
        if (ch is null) return new();
        var sources = await _db.Sources.ToListAsync(ct);
        var disabled = sources.Where(s => !s.Enabled).Select(s => s.Id).ToHashSet();

        // Provider family = same normalised host. Two logins with the SAME provider share a catalogue, so their
        // stream ids are directly comparable; any other provider's id 501 is a different channel entirely.
        static string HostOf(Data.Entities.ProviderSource s) =>
            (s.BaseUrl ?? "").Trim().ToLowerInvariant()
                .Replace("https://", "", StringComparison.Ordinal).Replace("http://", "", StringComparison.Ordinal)
                .TrimEnd('/');
        var myHost = sources.FirstOrDefault(s => s.Id == ch.SourceId) is { } mine ? HostOf(mine) : "";
        var familySourceIds = string.IsNullOrEmpty(myHost)
            ? new HashSet<int>()
            : sources.Where(s => HostOf(s) == myHost).Select(s => s.Id).ToHashSet();

        // An empty normalised key/name must NOT act as a wildcard (junk channels like "||" or "UK:" normalise to ""),
        // or an empty-named target would cross-match arbitrary other-source channels and re-home a recording to the
        // wrong channel. Only match LogicalKey/NameNorm when they're non-empty; StreamId equality stands only
        // within the provider family.
        var hasKey = !string.IsNullOrEmpty(ch.LogicalKey);
        var hasName = !string.IsNullOrEmpty(ch.NameNorm);
        var rows = await _db.Channels
            .Where(c => c.Enabled && c.SourceId != ch.SourceId &&
                        ((familySourceIds.Contains(c.SourceId) && c.StreamId == ch.StreamId)
                         || (hasKey && c.LogicalKey == ch.LogicalKey)
                         || (hasName && c.NameNorm == ch.NameNorm)))
            .ToListAsync(ct);

        int Quality(Channel c) => familySourceIds.Contains(c.SourceId) && c.StreamId == ch.StreamId ? 3
            : (hasKey && c.LogicalKey == ch.LogicalKey ? 2 : 1);

        return rows.Where(c => !disabled.Contains(c.SourceId))
            .GroupBy(c => c.SourceId)
            .Select(g => g.OrderByDescending(Quality).First()) // best-matching row per credential
            .Select(c => new ResolvedChannel(c.Id, c.SourceId, c.StreamId, c.Name, 0, 0))
            .ToList();
    }

    private static readonly Regex NonAlnum = new(@"[^a-z0-9 ]", RegexOptions.Compiled);

    /// <summary>Token-set Jaccard similarity 0..1 on normalised titles (lightweight fuzzy match). Public so the
    /// replay-rescue sweep can score EPG programme titles against a failed event's title with the same metric.</summary>
    public static double Similarity(string? a, string? b)
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

    private static readonly string[] VsSeparators = { " vs ", " vs. ", " v ", " @ ", " x ", " - ", " – " };

    /// <summary>Split an event title "Home vs Away" into each side's significant tokens, for a "does this programme
    /// show THIS game (both teams), not just this team" test. With no separator both sides get the whole title's
    /// tokens (single-name events — motorsport etc. — still match on their own tokens); the two returned sets are the
    /// SAME instance in that case, so a caller can detect a single-sided title via ReferenceEquals.</summary>
    public static (HashSet<string> A, HashSet<string> B) EventSides(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) { var empty = new HashSet<string>(); return (empty, empty); }
        foreach (var sep in VsSeparators)
        {
            var i = title.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && i + sep.Length < title.Length) return (Tokens(title[..i]), Tokens(title[(i + sep.Length)..]));
        }
        var all = Tokens(title);
        return (all, all);
    }

    /// <summary>True when a programme title shows THIS specific matchup, not just one of the teams generically.
    /// For a two-sided event each side must be evidenced by a token UNIQUE to it (audit EPG-01): a token both sides
    /// share — "United", "City", "FC" — is evidence for NEITHER, otherwise "Manchester United TV" would satisfy both
    /// Manchester United AND Newcastle United on the shared "united" alone. A side with no unique token at all can't
    /// be proven, so the match is refused rather than guessed.</summary>
    public static bool ShowsBothTeams(string? progTitle, HashSet<string> sideA, HashSet<string> sideB)
    {
        if (sideA.Count == 0 && sideB.Count == 0) return false;
        var t = Tokens(progTitle);
        if (t.Count == 0) return false;
        // Single-sided event (EventSides returned the same instance for both) — plain overlap is all we can ask.
        if (ReferenceEquals(sideA, sideB)) return sideA.Overlaps(t);
        if (sideA.Count == 0 || sideB.Count == 0) return false;
        bool aProven = false, bProven = false;
        foreach (var tok in t)
        {
            var inA = sideA.Contains(tok);
            var inB = sideB.Contains(tok);
            if (inA && !inB) aProven = true;
            else if (inB && !inA) bProven = true;
            if (aProven && bProven) return true;
        }
        return false;
    }
}
