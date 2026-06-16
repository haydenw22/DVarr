using System.Collections.Concurrent;
using DVarr.Data;
using DVarr.Data.Entities;
using DVarr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DVarr.Services.Tuner;

/// <summary>
/// Enforces the provider's hard rule: ONE concurrent stream per credential (docs/07).
/// Each source has a single in-memory slot (SemaphoreSlim(1,1)); a recording, live view,
/// probe, or relay all consume that one slot. Concurrency across the system therefore
/// equals the number of credentials. The TunerLease table mirrors the slot for
/// observability and is reconciled on boot. Singleton.
/// </summary>
public sealed class TunerLeaseManager
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _slots = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly DbWriteGate _gate;
    private readonly ILogger<TunerLeaseManager> _log;

    public TunerLeaseManager(IServiceScopeFactory scopes, DbWriteGate gate, ILogger<TunerLeaseManager> log)
    {
        _scopes = scopes;
        _gate = gate;
        _log = log;
    }

    private SemaphoreSlim SlotFor(int sourceId) => _slots.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));

    /// <summary>True if the credential's single slot is currently free.</summary>
    public bool IsFree(int sourceId) => SlotFor(sourceId).CurrentCount > 0;

    public int BusyCredentialCount => _slots.Values.Count(s => s.CurrentCount == 0);

    /// <summary>Try to take the credential's one slot. Returns the lease, or null if busy.</summary>
    public async Task<TunerLease?> TryAcquireAsync(int sourceId, LeasePurpose purpose,
        int? recordingId, int? channelId, int? streamId, CancellationToken ct = default)
    {
        var slot = SlotFor(sourceId);
        if (!await slot.WaitAsync(0, ct)) return null;

        try
        {
            TunerLease lease = new()
            {
                SourceId = sourceId,
                Purpose = purpose,
                RecordingId = recordingId,
                ChannelId = channelId,
                StreamId = streamId,
                AcquiredAtUtc = EpochTime.Now(),
                LastHeartbeatUtc = EpochTime.Now(),
                State = LeaseState.Active,
            };
            await _gate.WriteAsync(async () =>
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                db.TunerLeases.Add(lease);
                await db.SaveChangesAsync(ct);
            }, ct);
            _log.LogInformation("[Tuner] Acquired slot on source {Src} for {Purpose} (rec {Rec})", sourceId, purpose, recordingId);
            return lease;
        }
        catch
        {
            slot.Release();
            throw;
        }
    }

    public async Task ReleaseAsync(TunerLease lease, CancellationToken ct = default)
    {
        // Exactly-once per lease: a second release must NOT touch the semaphore, or it would free a slot another
        // session just acquired (breaking one-stream-per-credential). Gate the whole method on an atomic flag.
        if (Interlocked.Exchange(ref lease.ReleasedFlag, 1) != 0) return;
        try
        {
            await _gate.WriteAsync(async () =>
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
                var row = await db.TunerLeases.FindAsync(new object?[] { lease.Id }, ct);
                if (row is { State: LeaseState.Active })
                {
                    row.State = LeaseState.Released;
                    await db.SaveChangesAsync(ct);
                }
            }, ct);
        }
        finally
        {
            // Each acquire took the slot once; release it once. (SemaphoreFull guard is a belt-and-suspenders no-op.)
            if (_slots.TryGetValue(lease.SourceId, out var slot))
                try { slot.Release(); } catch (SemaphoreFullException) { }
            _log.LogInformation("[Tuner] Released slot on source {Src} (lease {Id})", lease.SourceId, lease.Id);
        }
    }

    public async Task HeartbeatAsync(int leaseId, long bytesWritten, CancellationToken ct = default)
    {
        await _gate.WriteAsync(async () =>
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var row = await db.TunerLeases.FindAsync(new object?[] { leaseId }, ct);
            if (row is { State: LeaseState.Active })
            {
                row.LastHeartbeatUtc = EpochTime.Now();
                row.BytesWritten = bytesWritten;
                await db.SaveChangesAsync(ct);
            }
        }, ct);
    }

    /// <summary>
    /// On boot the process holds no real streams, so any Active lease is stale: mark it
    /// Orphaned and start with all slots free (docs/05 §3.4 lease reconciliation).
    /// </summary>
    public async Task ReconcileOnBootAsync(CancellationToken ct = default)
    {
        await _gate.WriteAsync(async () =>
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DVarrDbContext>();
            var stale = await db.TunerLeases.Where(l => l.State == LeaseState.Active).ToListAsync(ct);
            foreach (var l in stale) l.State = LeaseState.Orphaned;
            if (stale.Count > 0) await db.SaveChangesAsync(ct);
            if (stale.Count > 0) _log.LogWarning("[Tuner] Reconciled {N} stale active lease(s) on boot", stale.Count);
        }, ct);
        _slots.Clear();
    }
}
