using DVarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Data;

/// <summary>
/// Re-points a recording onto a different credential/channel/stream — the ONLY safe way to change
/// <see cref="Recording.SourceId"/>.
///
/// <para><c>SourceId</c> is part of the <c>(Id, SourceId)</c> ALTERNATE KEY on <see cref="Recording"/> — the
/// principal of the composite FK that structurally pins a recording's fallbacks to the SAME credential (the bug #7
/// fix; see <see cref="RecordingFallback"/>). EF Core forbids changing a key property on a TRACKED entity, so the
/// natural <c>recording.SourceId = x</c> throws <em>"The property 'Recording.SourceId' is part of a key and so cannot
/// be modified"</em> the moment the value actually changes — e.g. boot-recovery spreading to another login, a
/// cross-login reassign, or a re-resolve onto a different source's channel.</para>
///
/// <para>This applies the change the safe way: (1) delete the recording's fallbacks — their composite FK to the old
/// <c>(Id, SourceId)</c> would otherwise dangle when the principal key moves; (2) issue a tracker-bypassing SQL
/// UPDATE. The caller must already hold the <see cref="Infrastructure.DbWriteGate"/>, and is responsible for re-adding
/// any fallbacks (now matching the new SourceId) and persisting other fields with its own SaveChanges.</para>
///
/// <para>IMPORTANT: this does NOT update an in-memory tracked copy of the recording (ExecuteUpdate bypasses the
/// change tracker). After calling, use the new values directly, or re-read/<see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions"/>
/// reload the entity, rather than reading the now-stale tracked instance's SourceId/ChannelId/StreamId.</para>
/// </summary>
public static class RecordingRepoint
{
    public static async Task ApplyAsync(DVarrDbContext db, int recordingId, int sourceId, int channelId, int streamId, long nowUtc, CancellationToken ct = default)
    {
        // FK ordering: drop the dependent fallbacks BEFORE moving the principal's alt-key (SQLite enforces FKs).
        await db.RecordingFallbacks.Where(f => f.RecordingId == recordingId).ExecuteDeleteAsync(ct);
        // Tracker-bypassing UPDATE — the only way to change the alt-key SourceId without EF's key-immutability error.
        await db.Recordings.Where(r => r.Id == recordingId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.SourceId, sourceId)
            .SetProperty(r => r.ChannelId, channelId)
            .SetProperty(r => r.StreamId, streamId)
            .SetProperty(r => r.UpdatedUtc, nowUtc), ct);
    }
}
