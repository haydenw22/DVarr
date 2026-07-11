using System.Collections.Concurrent;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using DVarr.Services.Ingest;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

/// <summary>
/// Arm-window EPG re-pick (docs: "map Fox 503+504 and record whichever the guide says has the game"). The resolver
/// already scores mapped channels by guide-title similarity, but it runs at PLACEMENT — days before the provider's
/// guide can see the event, so the pick degenerates to rank order. This service re-runs that same resolver ≈1h before
/// start (the sweep horizon — the provider's EPG often has no content 24h out) and re-points the recording (SAME
/// credential only — slot planning is untouched) when another mapped channel's guide actually shows the event.
/// It also keeps the guide fresh: if this source's last successful EPG sync is >12h old (or never), it kicks a
/// background refresh before re-picking (the next sweep re-picks against the fresh data); and if the guide is blank for
/// every mapped channel close to start it does the same. Both are rate-limited by a shared 30-min per-source cooldown,
/// and the ingest's per-source semaphore + last-known-good swap make an opportunistic refresh safe. Manual choices are
/// respected via Recording.ChannelLocked.
/// </summary>
public sealed class EpgRepickService
{
    // Tuning (constants, not settings — one kill-switch setting `epg_repick_enabled` governs the feature):
    private const double MinEpgScore = 0.25;   // proposed channel must actually look like the event
    private const double Hysteresis = 0.10;    // and beat the current channel by this much (no tick-to-tick flapping)
    private const int BlankRefreshWindowS = 3600;      // only chase a blank guide when the event is this close (matches the sweep horizon)
    private const int StaleEpgS = 12 * 3600;           // refresh the source's guide if its last good sync is older than this
    private const int RefreshCooldownS = 30 * 60;      // at most one opportunistic EPG refresh per source per 30 min

    // Per-source last opportunistic-refresh stamp (static: the service is scoped per tick/request).
    private static readonly ConcurrentDictionary<int, long> _lastRefresh = new();

    private readonly DVarrDbContext _db;
    private readonly ResolverService _resolver;
    private readonly EpgIngestService _epg;
    private readonly DbWriteGate _gate;
    private readonly SettingsService _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EpgRepickService> _log;

    public EpgRepickService(DVarrDbContext db, ResolverService resolver, EpgIngestService epg, DbWriteGate gate,
        SettingsService settings, IServiceScopeFactory scopes, ILogger<EpgRepickService> log)
    { _db = db; _resolver = resolver; _epg = epg; _gate = gate; _settings = settings; _scopes = scopes; _log = log; }

    /// <summary>Re-pick the recording's channel from the live guide. Returns true if the recording was re-pointed.
    /// Safe no-op for manual recordings (no event), non-Pending states, locked channels, or when disabled.</summary>
    public async Task<bool> TryRepickAsync(int recordingId, CancellationToken ct = default)
    {
        var rec = await _db.Recordings.FindAsync(new object?[] { recordingId }, ct);
        if (rec is null || rec.State != RecordingState.Pending || rec.EventId is not { } eventId || rec.ChannelLocked) return false;
        if (!await _settings.GetBoolAsync("epg_repick_enabled")) return false;
        var ev = await _db.Events.FindAsync(new object?[] { eventId }, ct);
        if (ev is null) return false;

        var now = EpochTime.Now();

        // Stale-guide refresh: the provider's EPG often has no content 24h out, so re-picking is worthless against an
        // old guide. If this source's last successful EPG sync is >12h old (or it never synced), kick a background
        // refresh (shared 30-min per-source cooldown guards against repeated kicks) and re-pick against the current data
        // anyway — the next sweep after the refresh lands re-picks against the fresh guide.
        var src = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rec.SourceId, ct);
        if (src is not null && (src.LastEpgSyncUtc is not { } last || now - last > StaleEpgS))
            KickEpgRefresh(rec.SourceId, ev.Title);

        var res = await _resolver.ResolveAsync(eventId, ct, restrictSourceId: rec.SourceId);

        // Blank-guide chase: if NO mapped channel on this credential has any programme overlapping the event window
        // and the event is close, kick a background refresh of that source's guide (rate-limited; the next sweep then
        // re-picks against fresh data). Detected explicitly — a zero EPG score alone could just mean bad titles.
        if (ev.StartUtc > now && ev.StartUtc - now <= BlankRefreshWindowS && await GuideBlankAsync(rec.SourceId, ev, ct))
            KickEpgRefresh(rec.SourceId, ev.Title);

        if (!res.Ok || res.Primary is null) return false;
        var all = new List<ResolvedChannel> { res.Primary };
        all.AddRange(res.Fallbacks);
        // The guide's pick: among the mapped candidates, the channel whose EPG best matches the event (total score
        // breaks ties). Judging by EpgScore — not total score — is what lets a nationally-broadcast game move OFF a
        // pinned or team-scoped channel whose own guide doesn't show it (issue #5): the pin/team scope governs
        // placement, the guide governs where the game actually airs. The MinEpgScore + Hysteresis gates below mean a
        // channel whose guide DOES show the event is never abandoned on a weak or merely-similar match elsewhere.
        var best = all.OrderByDescending(c => c.EpgScore).ThenByDescending(c => c.Score).First();
        var curEpg = all.FirstOrDefault(c => c.ChannelId == rec.ChannelId)?.EpgScore ?? 0;
        if (best.ChannelId == rec.ChannelId) return false;                       // already on the guide's pick
        if (best.EpgScore < MinEpgScore || best.EpgScore < curEpg + Hysteresis) return false; // move only for a real guide reason

        var fromName = (await _db.Channels.FindAsync(new object?[] { rec.ChannelId }, ct))?.Name ?? $"#{rec.ChannelId}";
        var moved = false;
        await _gate.WriteAsync(async () =>
        {
            // Re-check inside the gate: the recording may have armed/cancelled since the un-gated read.
            var fresh = await _db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordingId);
            if (fresh is null || fresh.State != RecordingState.Pending || fresh.ChannelLocked || fresh.ChannelId != rec.ChannelId) return;
            // Same-credential re-point (RecordingRepoint deletes the old fallback ladder; rebuild it from this resolve).
            await RecordingRepoint.ApplyAsync(_db, recordingId, best.SourceId, best.ChannelId, best.StreamId, now);
            var rank = 2;
            foreach (var fb in res.Fallbacks.Where(f => f.ChannelId != best.ChannelId).DistinctBy(f => f.ChannelId))
                _db.RecordingFallbacks.Add(new RecordingFallback { RecordingId = recordingId, Rank = rank++, ChannelId = fb.ChannelId, SourceId = best.SourceId });
            _db.Notifications.Add(new Notification
            {
                RecordingId = recordingId, TsUtc = now, Kind = NotificationKind.EpgRepick, Severity = Severity.Info,
                Message = $"guide match: '{ev.Title}' is on '{best.ChannelName}' (EPG {best.EpgScore:0.00} vs {curEpg:0.00} on '{fromName}') — recording moved",
            });
            await _db.SaveChangesAsync(ct);
            moved = true;
        }, ct);
        if (!moved) return false;

        // RecordingRepoint bypasses the change tracker, so the tracked `rec` is stale — detach it so a caller in the
        // SAME scope (e.g. RecorderService.TryStartAsync re-loading the row) reads the re-pointed values from the DB.
        _db.Entry(rec).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        _log.LogInformation("[EpgRepick] '{Title}' moved {From} → {To} (EPG {New:0.00} vs {Cur:0.00})", ev.Title, fromName, best.ChannelName, best.EpgScore, curEpg);
        return true;
    }

    /// <summary>True when no channel mapped to the event's league (on this credential) has ANY programme overlapping
    /// the event window — i.e. the guide can't see the event yet (stale/blank), as opposed to merely not matching.</summary>
    private async Task<bool> GuideBlankAsync(int sourceId, Event ev, CancellationToken ct)
    {
        var chIds = await _db.LeagueChannelMaps.Where(m => m.LeagueId == ev.LeagueId).Select(m => m.ChannelId).ToListAsync(ct);
        if (chIds.Count == 0) return false;
        var effIds = (await _db.Channels.Where(c => chIds.Contains(c.Id) && c.SourceId == sourceId).Select(c => new { c.EpgChannelId, c.MatchedEpgId }).ToListAsync(ct))
            .Select(c => !string.IsNullOrEmpty(c.EpgChannelId) ? c.EpgChannelId : c.MatchedEpgId)
            .Where(e => !string.IsNullOrEmpty(e)).Select(e => e!).Distinct().ToList();
        if (effIds.Count == 0) return true; // no channel even linked to the guide → a refresh (re-runs name-matching) may fix it
        var winStart = ev.StartUtc - 1800;
        var winEnd = ev.EndUtc ?? ev.StartUtc + 7200;
        return !await _db.Programmes.AnyAsync(p => p.SourceId == sourceId && effIds.Contains(p.EpgChannelId) && p.StopUtc > winStart && p.StartUtc < winEnd, ct);
    }

    private void KickEpgRefresh(int sourceId, string eventTitle)
    {
        var now = EpochTime.Now();
        var last = _lastRefresh.GetOrAdd(sourceId, 0);
        if (now - last < RefreshCooldownS || !_lastRefresh.TryUpdate(sourceId, now, last)) return;
        _log.LogInformation("[EpgRepick] guide blank around '{Title}' — refreshing source {Id} EPG in the background", eventTitle, sourceId);
        _ = Task.Run(async () =>
        {
            try
            {
                // Own scope: this outlives the caller's request/tick scope. The ingest's per-source semaphore fast-fails
                // if a sync is already running, and last-known-good keeps the old guide on any failure.
                using var scope = _scopes.CreateScope();
                var epg = scope.ServiceProvider.GetRequiredService<EpgIngestService>();
                var r = await epg.SyncSourceEpgAsync(sourceId, CancellationToken.None);
                _log.LogInformation("[EpgRepick] opportunistic EPG refresh source {Id}: {Status}", sourceId, r.Ok ? $"ok ({r.Programmes} programmes)" : r.Error);
            }
            catch (Exception ex) { _log.LogWarning(ex, "[EpgRepick] opportunistic EPG refresh failed for source {Id}", sourceId); }
        });
    }
}
