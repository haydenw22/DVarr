using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DVarr.Infrastructure;

/// <summary>
/// Applies the docs/05 §4.1 persistence pragmas on every SQLite connection open.
/// WAL + busy_timeout=15000 + synchronous=NORMAL + foreign_keys=ON is the configuration
/// that makes the original's "database is locked" storms impossible (combined with the
/// single-writer <see cref="DbWriteGate"/>). foreign_keys must be set per-connection
/// because SQLite resets it each open — hence an interceptor rather than one-time setup.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string PragmaSql =
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA busy_timeout=15000;" +
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA foreign_keys=ON;" +
        "PRAGMA wal_autocheckpoint=1000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaSql;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
