using System.Collections.Concurrent;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Events;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Recording;

/// <summary>
/// Smart auto-stop (Phase 21). Fixed windows cut off two World Cup games that went to extra time/penalties —
/// this service watches TheSportsDB's live status near a recording's scheduled end and EXTENDS the window in
/// 15-minute steps while the event is still in play (or its status is unknown — fail LONG, never clip a live
/// game), then closes it back down once the guide reports a terminal status (FT/AET/AP…). The running
/// <see cref="RecorderSupervisor"/> re-reads Recording.EndUtc live, so an extension takes effect mid-capture.
///
/// Guarantees:
///  - NEVER shortens a window below the scheduled end (Event.EndUtc) — a terminal status only trims an
///    extension back, and the post-pad always still runs ("keep full race endings" stays intact).
///  - Total extension is capped (per-league override, else 2h motorsport / 1h other sports) AND never eats
///    into the NEXT recording's pre-roll on the same credential (1 stream per login).
///  - Per-league opt-out (League.AutoStopMode = "fixed") and a global kill-switch (auto_stop_enabled).
///  - TSDB polling is throttled: one event lookup per recording per 120s, one livescore call per sport per 120s.
/// </summary>
public sealed class AutoStopService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DbWriteGate _gate;
    private readonly ILogger<AutoStopService> _log;

    /// <summary>Recording states an auto-stop decision may apply to — every "capture is (or is about to be) live"
    /// state. Terminal/finalizing states are never touched (extending a Stopping/Finalizing row is meaningless).</summary>
    private static readonly RecordingState[] ActiveStates =
    {
        RecordingState.Starting, RecordingState.Recording, RecordingState.Recovering,
        RecordingState.FailingOver, RecordingState.Degraded,
    };

    /// <summary>Guide statuses that mean the event is OVER (case-insensitive, trimmed). Soccer: FT / AET / AP
    /// (after penalties) / PEN; generic feeds also emit the spelled-out forms; AW/WO = awarded/walk-over.</summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "FT", "AET", "AP", "PEN", "Match Finished", "Finished", "Full Time", "Race Finished",
        "Cancelled", "Postponed", "Abandoned", "AW", "WO",
    };

    /// <summary>Guide statuses that mean the event is IN PLAY (1st/2nd half, half-time, extra time, penalty
    /// shoot-out, generic Live). Presence in the livescore feed or a non-empty strProgress also counts.</summary>
    private static readonly HashSet<string> InPlayStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1H", "2H", "HT", "ET", "BREAK", "P", "Live",
    };

    private enum StatusClass { Terminal, InPlay, Unknown }

    /// <summary>Per-recording auto-stop bookkeeping. Static so it survives the (singleton) service instance the same
    /// way AutoScheduleService's warn stamps do; pruned by <see cref="Prune"/>. Only the single tick loop mutates it.</summary>
    private sealed class RecTrack
    {
        public long LastPollUtc;          // last TSDB lookup/event poll for this recording (≥120s apart)
        public string? LastStatus;        // last strStatus seen from lookup/event (cached between polls)
        public long? OriginalEndFallback; // first-seen Recording.EndUtc — cap base ONLY when Event.EndUtc is null
        public bool CapWarned;            // "cannot extend further" already notified (one Warn, not one per tick)
        public long LastSeenUtc;          // last tick this recording was a candidate (prune key)
    }

    // Last-poll stamps keyed by recording id (event lookups) / lowercased sport (livescore), per the 120s throttle.
    private static readonly ConcurrentDictionary<int, RecTrack> _track = new();
    private static readonly ConcurrentDictionary<string, long> _liveScoreStamp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Dictionary<string, TsdbLiveScore>> _liveScoreIndex = new(StringComparer.OrdinalIgnoreCase);

    private const int PollAheadS = 600;      // start watching a recording this long before its scheduled end
    private const int DecideAheadS = 120;    // decisions (extend/clamp) only this close to the current end
    private const int ExtendStepS = 900;     // one extension step (15 min)
    private const int PollIntervalS = 120;   // min seconds between TSDB calls per recording / per sport
    private const int SlotGuardS = 120;      // safety gap kept before the next recording's pre-roll on the credential

    public AutoStopService(IServiceScopeFactory scopes, DbWriteGate gate, ILogger<AutoStopService> log)
    { _scopes = scopes; _gate = gate; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        _log.LogInformation("[AutoStop] Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "[AutoStop] Tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var tsdb = scope.ServiceProvider.GetRequiredService<TheSportsDbClient>();

        if (!await settings.GetBoolAsync("auto_stop_enabled")) return;

        var now = EpochTime.Now();

        // Candidates: live captures linked to an event, in an Auto (not "fixed") league, near/past their scheduled
        // end. No upper bound on `now` — a capture deep in its post-pad can still be extended if penalties run on.
        var candidates = await (from r in db.Recordings.AsNoTracking()
                                join e in db.Events.AsNoTracking() on r.EventId equals e.Id
                                join l in db.Leagues.AsNoTracking() on e.LeagueId equals l.Id
                                where ActiveStates.Contains(r.State)
                                      && (l.AutoStopMode == null || l.AutoStopMode == "auto")
                                      && now >= r.EndUtc - PollAheadS
                                select new
                                {
                                    r.Id, RecEndUtc = r.EndUtc, r.PostPadS, r.SourceId,
                                    e.TsdbEventId, e.Title, EventEndUtc = e.EndUtc,
                                    l.Sport, l.AutoStopMaxExtendS,
                                }).ToListAsync(ct);
        if (candidates.Count == 0) { Prune(now); return; }

        // One livescore call per distinct sport per PollIntervalS — presence in the feed counts as in-play, and it
        // carries strProgress (the match minute) for the notification text. Failures leave a (possibly stale) index;
        // classification falls back to the per-event lookup below, so livescore is purely additive.
        foreach (var sport in candidates.Select(c => c.Sport).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = sport.Trim().ToLowerInvariant();
            var last = _liveScoreStamp.GetOrAdd(key, 0);
            if (now - last < PollIntervalS) continue;
            _liveScoreStamp[key] = now; // stamp first so a throwing/slow provider isn't hammered every 30s
            try
            {
                var live = await tsdb.GetLiveScoresAsync(key, ct);
                _liveScoreIndex[key] = live.GroupBy(x => x.EventId, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoStop] livescore fetch failed for sport '{Sport}'", key); }
        }

        foreach (var c in candidates)
        {
            // Per-candidate isolation: one bad row / provider hiccup must never kill the whole tick (which would
            // silently stop protecting every other live recording).
            try
            {
                var track = _track.GetOrAdd(c.Id, _ => new RecTrack());
                track.LastSeenUtc = now;
                // Cap base = the event's SCHEDULED end (events keep it; only the recording row is extended). A rare
                // legacy/manual event without EndUtc falls back to the first EndUtc we saw for the recording — fixed
                // per recording so repeated extensions can't ratchet the cap base upward.
                track.OriginalEndFallback ??= c.RecEndUtc;
                var originalEnd = c.EventEndUtc ?? track.OriginalEndFallback.Value;

                // Status poll: at most one TSDB event lookup per recording per PollIntervalS; the freshest known
                // status is cached so the 30s decision loop below never goes blind between polls.
                if (!string.IsNullOrWhiteSpace(c.TsdbEventId) && now - track.LastPollUtc >= PollIntervalS)
                {
                    track.LastPollUtc = now; // stamp first — a failing lookup must not retry every 30s
                    var tev = await tsdb.GetEventByIdAsync(c.TsdbEventId!, ct);
                    if (tev is not null) track.LastStatus = tev.Status;
                }

                TsdbLiveScore? live = null;
                if (!string.IsNullOrWhiteSpace(c.TsdbEventId) && !string.IsNullOrWhiteSpace(c.Sport)
                    && _liveScoreIndex.TryGetValue(c.Sport.Trim().ToLowerInvariant(), out var idx))
                    idx.TryGetValue(c.TsdbEventId!, out live);

                // Effective status: the livescore entry is the freshest signal when present; else the event lookup.
                var status = !string.IsNullOrWhiteSpace(live?.Status) ? live!.Status : track.LastStatus;
                var cls = Classify(status, live);

                // Decisions only just before the current end — and Auto NEVER shortens a window: the terminal branch
                // only trims a previous EXTENSION back (never below the scheduled end), the in-play branch only grows.
                if (now < c.RecEndUtc - DecideAheadS) continue;

                if (cls == StatusClass.Terminal)
                {
                    // Finished per guide. If we previously extended past the scheduled end and that extension is
                    // still ahead of the clock, clamp back to now (never below the scheduled end) so the capture
                    // stops after its normal post-pad instead of riding out the whole unused extension.
                    if (c.RecEndUtc > originalEnd && now < c.RecEndUtc)
                    {
                        var clampTo = Math.Max(now, originalEnd);
                        if (clampTo < c.RecEndUtc)
                            await ClampAsync(db, c.Id, c.RecEndUtc, clampTo, status, now, ct);
                    }
                    // Never extended (or already elapsed): nothing to do — the fixed window + post-pad already covers it.
                }
                else // InPlay or Unknown — unknown fails LONG (F1/motorsport report no strStatus at all)
                {
                    var cap = c.AutoStopMaxExtendS ?? (MotorsportSession.IsMotorsport(c.Sport) ? 7200 : 3600);
                    var allowedEnd = originalEnd + cap;

                    // Slot boundary: never let this window (incl. post-pad) reach into the NEXT recording's pre-roll
                    // on the same credential (1 stream/login) — queried fresh each step so a cancelled blocker frees us.
                    var next = await db.Recordings.AsNoTracking()
                        .Where(n => n.Id != c.Id && n.SourceId == c.SourceId
                                    && (n.State == RecordingState.Pending || n.State == RecordingState.Starting)
                                    && n.EndUtc + n.PostPadS > now)
                        .OrderBy(n => n.StartUtc - n.PrePadS)
                        .Select(n => new { n.StartUtc, n.PrePadS })
                        .FirstOrDefaultAsync(ct);
                    if (next is not null)
                        allowedEnd = Math.Min(allowedEnd, next.StartUtc - next.PrePadS - SlotGuardS - c.PostPadS);

                    var newEnd = Math.Min(c.RecEndUtc + ExtendStepS, allowedEnd);
                    if (newEnd <= c.RecEndUtc)
                    {
                        // Step clipped to zero (cap reached or the credential is needed) — warn ONCE, then let the
                        // capture stop at its current window like a fixed recording.
                        if (!track.CapWarned)
                        {
                            track.CapWarned = true;
                            await NotifyAsync(db, c.Id, Severity.Warn,
                                $"cannot extend further (cap/slot boundary) — will stop at {EpochTime.ToBrisbane(c.RecEndUtc + c.PostPadS).ToString("HH:mm")}", ct);
                            _log.LogWarning("[AutoStop] Recording {Id} '{Title}': still in play but cannot extend further (cap/slot boundary)", c.Id, c.Title);
                        }
                    }
                    else
                    {
                        track.CapWarned = false; // a blocker freed / cap raised — future clips may warn again
                        await ExtendAsync(db, c.Id, c.RecEndUtc, newEnd, StatusText(status, live, cls), now, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoStop] failed to evaluate recording {Id}", c.Id); }
        }

        Prune(now);
    }

    /// <summary>TERMINAL / IN-PLAY / UNKNOWN per the spec sets. Terminal wins over livescore presence (a feed can
    /// briefly keep an FT row); livescore presence or a match-minute means in-play even with an unmapped status.</summary>
    private static StatusClass Classify(string? status, TsdbLiveScore? live)
    {
        var s = status?.Trim();
        if (!string.IsNullOrEmpty(s) && TerminalStatuses.Contains(s)) return StatusClass.Terminal;
        if (live is not null) return StatusClass.InPlay;
        if (!string.IsNullOrEmpty(s) && InPlayStatuses.Contains(s)) return StatusClass.InPlay;
        return StatusClass.Unknown; // empty / "Not Started" / anything unmapped
    }

    /// <summary>Human status for notifications: "2H 97'" / "ET" / "in play" (unknown). Progress is the livescore
    /// match minute when available.</summary>
    private static string StatusText(string? status, TsdbLiveScore? live, StatusClass cls)
    {
        var s = status?.Trim();
        if (string.IsNullOrEmpty(s)) s = cls == StatusClass.InPlay ? "live" : "in play";
        var p = live?.Progress?.Trim().TrimEnd('\'');
        return string.IsNullOrEmpty(p) ? s! : $"{s} {p}'";
    }

    /// <summary>Extend the recording's end to <paramref name="newEnd"/> (gate-written; re-verified against the
    /// snapshot so a stale candidate row can never double-apply a step) + an Info AutoExtended notification.</summary>
    private async Task ExtendAsync(DVarrDbContext db, int recId, long snapshotEnd, long newEnd, string statusText, long now, CancellationToken ct)
    {
        var applied = false;
        await _gate.WriteAsync(async () =>
        {
            var r = await db.Recordings.FindAsync(recId);
            if (r is null || !ActiveStates.Contains(r.State) || r.EndUtc != snapshotEnd || newEnd <= r.EndUtc) return;
            r.EndUtc = newEnd;
            r.UpdatedUtc = now;
            db.Notifications.Add(new Notification
            {
                RecordingId = recId, TsUtc = now, Kind = NotificationKind.AutoExtended, Severity = Severity.Info,
                Message = $"still in play ({statusText}) — extended to {EpochTime.ToBrisbane(newEnd).ToString("HH:mm")}",
            });
            await db.SaveChangesAsync(ct);
            applied = true;
        }, ct);
        if (applied)
            _log.LogInformation("[AutoStop] Recording {Id}: still in play ({Status}) — extended end to {End} (Brisbane {Bne})",
                recId, statusText, newEnd, EpochTime.ToBrisbane(newEnd).ToString("HH:mm"));
    }

    /// <summary>Trim an unused extension back to <paramref name="clampTo"/> (≥ the scheduled end, ≥ now — NEVER
    /// shortens below either) once the guide says the event finished; the post-pad then runs as normal. The
    /// notification is naturally deduped: it is written only when the clamp actually changes EndUtc.</summary>
    private async Task ClampAsync(DVarrDbContext db, int recId, long snapshotEnd, long clampTo, string? status, long now, CancellationToken ct)
    {
        var applied = false;
        await _gate.WriteAsync(async () =>
        {
            var r = await db.Recordings.FindAsync(recId);
            if (r is null || !ActiveStates.Contains(r.State) || r.EndUtc != snapshotEnd || clampTo >= r.EndUtc) return;
            r.EndUtc = clampTo;
            r.UpdatedUtc = now;
            db.Notifications.Add(new Notification
            {
                RecordingId = recId, TsUtc = now, Kind = NotificationKind.AutoExtended, Severity = Severity.Info,
                Message = $"finished per guide ({status?.Trim() ?? "final"}) — closing after post-pad",
            });
            await db.SaveChangesAsync(ct);
            applied = true;
        }, ct);
        if (applied)
            _log.LogInformation("[AutoStop] Recording {Id}: finished per guide ({Status}) — closing after post-pad", recId, status);
    }

    /// <summary>Standalone AutoExtended notification (no recording mutation).</summary>
    private async Task NotifyAsync(DVarrDbContext db, int recId, Severity sev, string message, CancellationToken ct)
    {
        await _gate.WriteAsync(async () =>
        {
            db.Notifications.Add(new Notification
            {
                RecordingId = recId, TsUtc = EpochTime.Now(), Kind = NotificationKind.AutoExtended, Severity = sev, Message = message,
            });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    /// <summary>Drop per-recording tracking not seen for 6h (its capture ended) and stale livescore sport caches,
    /// so the static state can't grow without bound across months of uptime.</summary>
    private static void Prune(long now)
    {
        foreach (var kv in _track)
            if (now - kv.Value.LastSeenUtc > 21600) _track.TryRemove(kv.Key, out _);
        foreach (var kv in _liveScoreStamp)
            if (now - kv.Value > 21600) { _liveScoreStamp.TryRemove(kv.Key, out _); _liveScoreIndex.TryRemove(kv.Key, out _); }
    }
}
