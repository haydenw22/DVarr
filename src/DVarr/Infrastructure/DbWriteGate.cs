namespace DVarr.Infrastructure;

/// <summary>
/// The single serialized writer (docs/05 §4.1). All DB writes route through this gate so
/// there is never more than one concurrent write transaction — the structural fix for
/// the legacy recorder's "database is locked" storms. Reads never take the gate (WAL lets readers
/// proceed during a write). Registered as a singleton.
/// </summary>
public sealed class DbWriteGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int QueueDepth => _gate.CurrentCount == 0 ? 1 : 0; // coarse: 0 free => a write holds it

    public async Task<T> WriteAsync<T>(Func<Task<T>> write, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await write(); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(Func<Task> write, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await write(); }
        finally { _gate.Release(); }
    }
}
