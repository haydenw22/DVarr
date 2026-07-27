using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Events;

/// <summary>Second-chance replay rescue: opens a rescue ticket when a monitored-league game ends with no playable
/// copy. The background <see cref="RescueSweepService"/> then hunts the guide for a re-air. Shared entry point so
/// all three terminal-failure sites (failed finalize, missed window, unresolved conflict) open tickets identically.</summary>
public static class RescueService
{
    /// <summary>Open a rescue ticket for a failed recording — if the feature is on, the recording is tied to a
    /// monitored-league event, no good copy already exists, and no ticket is already hunting this event. Best-effort
    /// and self-gating, so it's safe to call from any terminal-failure path.</summary>
    public static async Task TryOpenTicketAsync(DVarrDbContext db, DbWriteGate gate, SettingsService settings,
        int recordingId, string reason, ILogger log, CancellationToken ct = default, bool requireCorroborated = false)
    {
        if (!await settings.GetBoolAsync("replay_rescue_enabled")) return;

        var rec = await db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordingId, ct);
        if (rec is null || rec.EventId is not { } eventId) return; // only linked events have a re-air search space
        if (rec.RescueTicketId is not null) return;                // a replay's own failure re-opens its parent, not a new ticket
        // A recording the guide never corroborated demands a VERIFIED rescue however it ended — its channel/window
        // is exactly what mustn't be blindly re-pulled from the archive (it would download the same wrong content).
        requireCorroborated = requireCorroborated || rec.GuideUncorroborated;

        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return;

        // Already hunting this event, or a good copy already exists → nothing to rescue.
        if (await db.RescueTickets.AsNoTracking().AnyAsync(t => t.EventId == eventId &&
                (t.State == RescueTicketState.Open || t.State == RescueTicketState.Scheduled), ct)) return;
        if (await HasGoodCopyAsync(db, eventId, ct)) return;

        var now = EpochTime.Now();
        var giveUpDays = await settings.GetIntAsync("replay_rescue_give_up_days"); if (giveUpDays <= 0) giveUpDays = 3;
        var wholeSource = await settings.GetBoolAsync("replay_rescue_whole_source");
        var end = ev.EndUtc ?? ev.StartUtc + 7200;

        await gate.WriteAsync(async () =>
        {
            // Re-check inside the gate (a concurrent failure path may have just opened one).
            if (await db.RescueTickets.AnyAsync(t => t.EventId == eventId &&
                    (t.State == RescueTicketState.Open || t.State == RescueTicketState.Scheduled), ct)) return;
            db.RescueTickets.Add(new RescueTicket
            {
                RecordingId = recordingId, EventId = eventId, LeagueId = ev.LeagueId,
                Title = ev.Title, MatchQuery = ev.Title,
                EventStartUtc = ev.StartUtc, EventEndUtc = end,
                State = RescueTicketState.Open, CreatedUtc = now, NextSweepUtc = now,
                ExpiresUtc = now + giveUpDays * 86400L, WholeSource = wholeSource, Note = reason,
                RequireCorroborated = requireCorroborated,
            });
            db.Notifications.Add(new Notification
            {
                RecordingId = recordingId, TsUtc = now, Kind = NotificationKind.ReplayHunting, Severity = Severity.Info,
                Message = requireCorroborated
                    ? $"“{ev.Title}” recorded with no guide corroboration — hunting the provider archive and re-airs for a verified copy (cancel the hunt from Recordings if the capture turned out fine)"
                    : $"hunting for a re-air of “{ev.Title}” — DVarr will record a replay if one shows in the guide within {giveUpDays} day(s)",
            });
            await db.SaveChangesAsync(ct);
        }, ct);
        log.LogInformation("[Rescue] opened replay ticket for event {Ev} '{Title}' ({Reason})", eventId, ev.Title, reason);
    }

    /// <summary>True when a playable copy of the event already exists — a finished (Done) recording, or a library
    /// file on disk. Used to avoid opening (or to close) a rescue ticket when the game is already safely captured.
    /// A copy that finalized with <see cref="Data.Entities.Recording.GuideUncorroborated"/> set does NOT count
    /// (nothing in the guide ever showed the fixture on the recorded channel — it very likely captured the wrong
    /// programme), and neither does that copy's library row.</summary>
    public static async Task<bool> HasGoodCopyAsync(DVarrDbContext db, int eventId, CancellationToken ct = default)
        => await db.Recordings.AsNoTracking().AnyAsync(r => r.EventId == eventId && r.State == RecordingState.Done && !r.GuideUncorroborated, ct)
           || await db.LibraryItems.AsNoTracking().AnyAsync(i => i.EventId == eventId && i.Status == LibraryItemStatus.Ok
                  && !db.Recordings.Any(r => r.Id == i.RecordingId && r.GuideUncorroborated), ct);
}
