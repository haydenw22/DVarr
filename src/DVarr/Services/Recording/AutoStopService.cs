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
/// The decisions themselves live in <see cref="AutoStopPolicy"/>.
///
/// Guarantees:
///  - Never shortens a window below the scheduled end (Event.EndUtc) — a terminal status only trims an
///    extension back, and the post-pad always still runs ("keep full race endings" stays intact). The one
///    exception is opt-in: auto_stop_early_on_final ends at a CONFIRMED final, post-pad still included.
///  - Only extends a capture that is actually running, holds (rather than extends) while a final is being
///    confirmed, and treats "not started" long after kick-off as a stale feed rather than a reason to extend.
///  - Total extension is capped (per-league override, else 2h motorsport / 1h other sports) AND never eats
///    into the NEXT recording's pre-roll on the same credential (1 stream per login).
///  - Per-league opt-out (League.AutoStopMode = "fixed") and a global kill-switch (auto_stop_enabled).
///  - TSDB polling is throttled: one event lookup per recording per 120s, one livescore call per sport per 120s.
/// </summary>
public sealed class AutoStopService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DbWriteGate _gate;
    private readonly RecorderService _recorder;
    private readonly ILogger<AutoStopService> _log;

    /// <summary>Recording states an auto-stop decision may apply to — every "capture is (or is about to be) live"
    /// state. Terminal/finalizing states are never touched (extending a Stopping/Finalizing row is meaningless).</summary>
    private static readonly RecordingState[] ActiveStates =
    {
        RecordingState.Starting, RecordingState.Recording, RecordingState.Recovering,
        RecordingState.FailingOver, RecordingState.Degraded,
    };

    /// <summary>Per-recording auto-stop bookkeeping. Static so it survives the (singleton) service instance the same
    /// way AutoScheduleService's warn stamps do; pruned by <see cref="Prune"/>. Only the single tick loop mutates it.</summary>
    private sealed class RecTrack
    {
        public long LastPollUtc;          // last TSDB lookup/event poll for this recording (≥120s apart)
        public string? LastStatus;        // last strStatus seen from lookup/event (cached between polls)
        public long? OriginalEndFallback; // first-seen Recording.EndUtc — cap base ONLY when Event.EndUtc is null
        public bool CapWarned;            // "cannot extend further" already notified (one Warn, not one per tick)
        public bool HoldWarned;           // "feed looks stale — holding" already notified
        public long LastSeenUtc;          // last tick this recording was a candidate (prune key)
        public long FirstTerminalUtc;     // terminal debounce: when a terminal status was FIRST seen (0 = not currently terminal)
        public string? LastMarkStatus;    // last raw status written to LiveMarksJson (dedupe transitions)
        public int MarkCount;             // marks written so far (bounded — a flapping feed can't grow the JSON forever)
    }

    // Last-poll stamps keyed by recording id (event lookups) / lowercased sport (livescore), per the 120s throttle.
    private static readonly ConcurrentDictionary<int, RecTrack> _track = new();
    private static readonly ConcurrentDictionary<string, long> _liveScoreStamp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Dictionary<string, TsdbLiveScore>> _liveScoreIndex = new(StringComparer.OrdinalIgnoreCase);

    private const int DecideAheadS = 120;    // decisions (extend/clamp) only this close to the current end
    private const int ExtendStepS = 900;     // one extension step (15 min)
    private const int PollIntervalS = 120;   // min seconds between TSDB calls per recording / per sport
    private const int SlotGuardS = 120;      // safety gap kept before the next recording's pre-roll on the credential
    /// <summary>A terminal status must persist this long (≥2 polls) before a clamp acts on it — real feeds
    /// briefly show "FT" at the end of regulation before flipping to ET/penalties, and clamping on that one
    /// bogus sample is exactly the missed-shootout failure auto-stop exists to prevent. Fail LONG.</summary>
    private const int TerminalConfirmS = 150;
    /// <summary>Marks cap per recording: far above any real match's transition count, only a backstop so a
    /// status-flapping feed can't grow LiveMarksJson without bound.</summary>
    private const int MaxMarks = 40;

    public AutoStopService(IServiceScopeFactory scopes, DbWriteGate gate, RecorderService recorder, ILogger<AutoStopService> log)
    { _scopes = scopes; _gate = gate; _recorder = recorder; _log = log; }

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

        var autoStop = await settings.GetBoolAsync("auto_stop_enabled");
        var chapterMarks = await settings.GetBoolAsync("chapter_marks_enabled");
        if (!autoStop && !chapterMarks) return;
        var stopEarlyOnFinal = await settings.GetBoolAsync("auto_stop_early_on_final");

        var now = EpochTime.Now();

        // Candidates: EVERY live event-linked capture, tracked from the moment it starts — chapter marks need
        // the kickoff/half-time transitions, not just the endgame. Extend/clamp DECISIONS still apply only near
        // the end (DecideAheadS below) and only for Auto (not "fixed") leagues with auto-stop enabled. No upper
        // bound on `now` — a capture deep in its post-pad can still be extended if penalties run on.
        // Catch-up (archive) pulls are excluded outright: their event already finished, so livescore reads
        // "terminal" and the clamp branch would cut the download window mid-pull. Their wall-clock window is
        // simply the download timeout — auto-stop has nothing to decide for them (chapters ride LiveMarksJson
        // captured by the ORIGINAL recording anyway).
        var candidates = await (from r in db.Recordings.AsNoTracking()
                                join e in db.Events.AsNoTracking() on r.EventId equals e.Id
                                join l in db.Leagues.AsNoTracking() on e.LeagueId equals l.Id
                                where ActiveStates.Contains(r.State) && r.CatchupSourceStartUtc == null
                                select new
                                {
                                    r.Id, RecEndUtc = r.EndUtc, r.PostPadS, r.SourceId, r.ChannelId,
                                    e.TsdbEventId, e.Title, EventStartUtc = e.StartUtc, EventEndUtc = e.EndUtc,
                                    l.Sport, l.AutoStopMaxExtendS, l.AutoStopMode,
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
                // Only a capture that is actually running can be extended. A row left in an active state with no
                // supervisor (or one already finalizing) used to collect extension after extension — five in two
                // minutes on 30 July for a recording that wasn't even running.
                if (!_recorder.IsActive(c.Id) || RecorderSupervisor.Finalizing.ContainsKey(c.Id)) continue;

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
                var cls = AutoStopPolicy.Classify(status, live is not null);

                // Chapter marks: persist every raw status TRANSITION (kick-off, HT, 2H, ET, penalties, FT…)
                // with its wall-clock — finalize turns them into embedded MKV chapters. Independent of the
                // extend/clamp decisions, so "fixed" auto-stop leagues still get chapters.
                if (chapterMarks)
                    await RecordMarkAsync(db, c.Id, track, status, live?.Progress, now, ct);

                // Terminal debounce: real feeds briefly show "FT" at the end of regulation before flipping to
                // ET/penalties. A terminal sample only counts once it has PERSISTED across polls; until then the
                // policy HOLDS (no clamp — and no extension either). Any non-terminal sighting resets it.
                if (cls == AutoStopPolicy.StatusClass.Terminal) { if (track.FirstTerminalUtc == 0) track.FirstTerminalUtc = now; }
                else track.FirstTerminalUtc = 0;
                var terminalConfirmed = track.FirstTerminalUtc != 0 && now - track.FirstTerminalUtc >= TerminalConfirmS;

                if (!autoStop || (c.AutoStopMode is not null && c.AutoStopMode != "auto")) continue;

                // Extension ceiling: the per-sport cap past the scheduled end, and never into the NEXT recording's
                // pre-roll on the same credential (1 stream/login) — queried fresh so a cancelled blocker frees us.
                // Only a genuinely UPCOMING live recording bounds the extension: catch-up pulls (a download timeout,
                // created with StartUtc=now) and anything whose padded window has already started are excluded.
                var cap = await settings.GetAutoStopCapSecondsAsync(c.Sport, c.AutoStopMaxExtendS);
                var allowedEnd = originalEnd + cap;
                var next = await db.Recordings.AsNoTracking()
                    .Where(n => n.Id != c.Id && n.SourceId == c.SourceId
                                && (n.State == RecordingState.Pending || n.State == RecordingState.Starting)
                                && n.CatchupSourceStartUtc == null
                                && n.StartUtc - n.PrePadS > now
                                && n.EndUtc + n.PostPadS > now)
                    .OrderBy(n => n.StartUtc - n.PrePadS)
                    .Select(n => new { n.StartUtc, n.PrePadS })
                    .FirstOrDefaultAsync(ct);
                if (next is not null)
                    allowedEnd = Math.Min(allowedEnd, next.StartUtc - next.PrePadS - SlotGuardS - c.PostPadS);

                // A stale "not started" may still extend as far as the broadcaster's own guide slot — looked up only
                // when that is the question being asked.
                long? guideSlotEnd = null;
                if (cls == AutoStopPolicy.StatusClass.NotStarted && now >= c.RecEndUtc - DecideAheadS
                    && now >= c.EventStartUtc + AutoStopPolicy.StaleNotStartedAfterS)
                    guideSlotEnd = await GuideSlotEndAsync(db, c.ChannelId, c.EventStartUtc, ct);

                var d = AutoStopPolicy.Decide(cls, terminalConfirmed, now, c.RecEndUtc, originalEnd, c.EventStartUtc,
                    allowedEnd, guideSlotEnd, stopEarlyOnFinal, DecideAheadS, ExtendStepS);
                switch (d.Kind)
                {
                    case AutoStopPolicy.ActionKind.Clamp:
                        await ClampAsync(db, c.Id, c.RecEndUtc, d.NewEnd, status, now, ct);
                        break;
                    case AutoStopPolicy.ActionKind.Extend:
                        track.CapWarned = false; // a blocker freed / cap raised — future clips may warn again
                        track.HoldWarned = false;
                        await ExtendAsync(db, c.Id, c.RecEndUtc, d.NewEnd, StatusText(status, live, cls), now, ct);
                        break;
                    case AutoStopPolicy.ActionKind.CapReached:
                        // Step clipped to zero (cap reached or the credential is needed) — warn ONCE, then let the
                        // capture stop at its current window like a fixed recording.
                        if (!track.CapWarned)
                        {
                            track.CapWarned = true;
                            await NotifyAsync(db, c.Id, Severity.Warn,
                                $"cannot extend further (cap/slot boundary) — will stop at {EpochTime.ToDisplay(c.RecEndUtc + c.PostPadS):HH:mm}", ct);
                            _log.LogWarning("[AutoStop] Recording {Id} '{Title}': still in play but cannot extend further (cap/slot boundary)", c.Id, c.Title);
                        }
                        break;
                    case AutoStopPolicy.ActionKind.None when cls == AutoStopPolicy.StatusClass.NotStarted && d.Why.Length > 0:
                        if (!track.HoldWarned)
                        {
                            track.HoldWarned = true;
                            await NotifyAsync(db, c.Id, Severity.Info,
                                $"the guide still says this game hasn't started, long after its start time — treating that as a stale feed and stopping at {EpochTime.ToDisplay(c.RecEndUtc + c.PostPadS):HH:mm}", ct);
                            _log.LogInformation("[AutoStop] Recording {Id} '{Title}': status '{Status}' {Min} min after the start — stale feed, not extending",
                                c.Id, c.Title, status, (now - c.EventStartUtc) / 60);
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoStop] failed to evaluate recording {Id}", c.Id); }
        }

        Prune(now);
    }

    /// <summary>Human status for notifications: "2H 97'" / "ET" / "in play" (unknown). Progress is the livescore
    /// match minute when available.</summary>
    private static string StatusText(string? status, TsdbLiveScore? live, AutoStopPolicy.StatusClass cls)
    {
        var s = status?.Trim();
        if (string.IsNullOrEmpty(s)) s = cls == AutoStopPolicy.StatusClass.InPlay ? "live" : "in play";
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
                Message = $"still in play ({statusText}) — extended to {EpochTime.ToDisplay(newEnd).ToString("HH:mm")}",
            });
            await db.SaveChangesAsync(ct);
            applied = true;
        }, ct);
        if (applied)
            _log.LogInformation("[AutoStop] Recording {Id}: still in play ({Status}) — extended end to {End} ({Local} local)",
                recId, statusText, newEnd, EpochTime.ToDisplay(newEnd).ToString("HH:mm"));
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

    /// <summary>Append a status-transition mark ({t,s,p}) to Recording.LiveMarksJson when the raw status
    /// changes. Deduped against the last written status and bounded by <see cref="MaxMarks"/>; a write failure
    /// is swallowed (chapters are best-effort — they must never disturb the capture).</summary>
    private async Task RecordMarkAsync(DVarrDbContext db, int recId, RecTrack track, string? status, string? progress, long now, CancellationToken ct)
    {
        var s = status?.Trim();
        if (string.IsNullOrEmpty(s)) return;
        if (string.Equals(track.LastMarkStatus, s, StringComparison.OrdinalIgnoreCase)) return;
        if (track.MarkCount >= MaxMarks) return;
        track.LastMarkStatus = s;
        track.MarkCount++;
        try
        {
            await _gate.WriteAsync(async () =>
            {
                var r = await db.Recordings.FindAsync(recId);
                if (r is null || !ActiveStates.Contains(r.State)) return;
                var marks = new List<Dictionary<string, object?>>();
                if (!string.IsNullOrWhiteSpace(r.LiveMarksJson))
                {
                    try { marks = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(r.LiveMarksJson!) ?? marks; }
                    catch { /* corrupt → start fresh */ }
                }
                if (marks.Count >= MaxMarks) return;
                marks.Add(new Dictionary<string, object?> { ["t"] = now, ["s"] = s, ["p"] = string.IsNullOrWhiteSpace(progress) ? null : progress!.Trim() });
                r.LiveMarksJson = System.Text.Json.JsonSerializer.Serialize(marks);
                r.UpdatedUtc = now;
                await db.SaveChangesAsync(ct);
            }, ct);
            _log.LogDebug("[AutoStop] Recording {Id}: status mark '{Status}' at {Now}", recId, s, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "[AutoStop] mark write failed for {Id}", recId); }
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

    /// <summary>End of the guide programme the recorded channel had airing ten minutes into the event — the slot the
    /// broadcaster set aside for the game. Null when the channel isn't linked to the guide or nothing is listed.</summary>
    private static async Task<long?> GuideSlotEndAsync(DVarrDbContext db, int channelId, long eventStart, CancellationToken ct)
    {
        var ch = await db.Channels.AsNoTracking().Where(x => x.Id == channelId)
            .Select(x => new { x.SourceId, Epg = x.EpgChannelId ?? x.MatchedEpgId }).FirstOrDefaultAsync(ct);
        if (ch?.Epg is not { Length: > 0 } epg) return null;
        var at = eventStart + 600;
        return await db.Programmes.AsNoTracking()
            .Where(p => p.SourceId == ch.SourceId && p.EpgChannelId == epg && p.StartUtc <= at && p.StopUtc > at)
            .OrderByDescending(p => p.StartUtc).Select(p => (long?)p.StopUtc).FirstOrDefaultAsync(ct);
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
