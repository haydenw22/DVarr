using DVarr.Data;

namespace DVarr.Services.Events;

/// <summary>
/// Credit (login)-aware placement for the auto-scheduler. Each enabled source is one stream slot (1 stream/login),
/// so this decides WHICH credential an event records on: two overlapping events should use BOTH logins (cross-login
/// spreading) instead of fighting over one and wasting the second. When demand exceeds every login it reports a
/// conflict, resolved by a priority ladder. Pure decision logic — AutoScheduleService performs the DB writes inside
/// the single-writer gate. Per-recording failover stays SAME-credential (a fallback can never cross a login); the
/// spread here is a one-time SCHEDULING choice, kept distinct from runtime failover (docs/06 §7).
/// </summary>
public sealed class CreditAwarePlanner
{
    private readonly ResolverService _resolver;

    public CreditAwarePlanner(ResolverService resolver) { _resolver = resolver; }

    /// <summary>A possible home for a recording: one channel on one credential, with that credential's fallback ladder.</summary>
    public sealed record Option(int SourceId, int ChannelId, int StreamId, string ChannelName, List<(int channelId, int sourceId)> Fallbacks);

    /// <summary>A padded window occupying a slot on a credential (an existing recording, or one placed earlier this tick).</summary>
    public sealed record Slot(int SourceId, long StartUtc, long EndUtc, int RecordingId, RecordingState State, PRank Rank);

    /// <summary>The outcome for one event: placed on Option (optionally preempting an incumbent), or a Conflict with a reason.</summary>
    public sealed record Decision(bool Placed, Option? Option, int? PreemptRecordingId, bool Conflict, string Reason);

    /// <summary>Conflict rank (HIGHER wins): priority class, then league priority, then earlier start, then lower id.</summary>
    public readonly record struct PRank(int Prio, int League, long NegStart, long NegId) : IComparable<PRank>
    {
        public int CompareTo(PRank o)
        {
            var c = Prio.CompareTo(o.Prio); if (c != 0) return c;
            c = League.CompareTo(o.League); if (c != 0) return c;
            c = NegStart.CompareTo(o.NegStart); if (c != 0) return c;
            return NegId.CompareTo(o.NegId);
        }
    }

    public static PRank MakeRank(RecordingPriority prio, int leaguePriority, long startUtc, int id)
        => new(PrioScore(prio), leaguePriority, -startUtc, -(long)id);

    private static int PrioScore(RecordingPriority p) => p switch
    {
        RecordingPriority.CantMiss => 2,
        RecordingPriority.Normal => 1,
        _ => 0, // Opportunistic
    };

    public static bool Overlaps(long aStart, long aEnd, long bStart, long bEnd) => aStart < bEnd && bStart < aEnd;

    /// <summary>
    /// Build the candidate homes for an event: the resolver's primary credential first, then the SAME logical channel
    /// on each OTHER enabled credential (the spread). Each spread option carries that credential's own fallback ladder
    /// (the primary's ranked fallbacks mapped to it), so failover still has somewhere same-credential to go.
    /// </summary>
    public async Task<List<Option>> OptionsForEventAsync(int eventId, CancellationToken ct)
    {
        var rr = await _resolver.ResolveAsync(eventId, ct);
        if (!rr.Ok || rr.Primary is null) return new();

        var opts = new List<Option>
        {
            new(rr.Primary.SourceId, rr.Primary.ChannelId, rr.Primary.StreamId, rr.Primary.ChannelName,
                rr.Fallbacks.Where(f => f.ChannelId != rr.Primary.ChannelId)
                            .Select(f => (f.ChannelId, f.SourceId)).Distinct().ToList()),
        };

        foreach (var e in await _resolver.EquivalentChannelsAsync(rr.Primary.ChannelId, ct))
        {
            if (opts.Any(o => o.SourceId == e.SourceId)) continue; // one option per credential
            var fbs = new List<(int channelId, int sourceId)>();
            foreach (var fb in rr.Fallbacks)
            {
                var onCred = (await _resolver.EquivalentChannelsAsync(fb.ChannelId, ct)).FirstOrDefault(x => x.SourceId == e.SourceId);
                if (onCred is not null && onCred.ChannelId != e.ChannelId && fbs.All(x => x.channelId != onCred.ChannelId))
                    fbs.Add((onCred.ChannelId, onCred.SourceId));
            }
            opts.Add(new Option(e.SourceId, e.ChannelId, e.StreamId, e.ChannelName, fbs));
        }
        return opts;
    }

    /// <summary>
    /// Decide where (if anywhere) this event records. First free credential among the options wins. If every option's
    /// credential is busy, preempt the lowest-ranked still-Pending incumbent that this candidate STRICTLY outranks
    /// (parking it in Conflict); otherwise the candidate itself loses → Conflict. Active recordings are never moved.
    /// </summary>
    public Decision Decide(List<Option> options, long winStart, long winEnd, PRank candRank, List<Slot> slots)
    {
        if (options.Count == 0) return new(false, null, null, true, "no resolvable channel for this event");

        foreach (var o in options)
            if (!slots.Any(s => s.SourceId == o.SourceId && Overlaps(winStart, winEnd, s.StartUtc, s.EndUtc)))
                return new(true, o, null, false, ""); // free credential

        // Every candidate credential is busy → try to preempt a strictly-lower-priority PENDING incumbent.
        Option? bestOpt = null; Slot? victim = null;
        foreach (var o in options)
            foreach (var s in slots.Where(s => s.SourceId == o.SourceId && s.State == RecordingState.Pending
                                               && Overlaps(winStart, winEnd, s.StartUtc, s.EndUtc)))
                if (candRank.CompareTo(s.Rank) > 0 && (victim is null || s.Rank.CompareTo(victim.Rank) < 0))
                { victim = s; bestOpt = o; }

        if (victim is not null && bestOpt is not null)
            return new(true, bestOpt, victim.RecordingId, false, $"preempted lower-priority recording #{victim.RecordingId}");

        return new(false, null, null, true, "both logins busy for this window");
    }
}
