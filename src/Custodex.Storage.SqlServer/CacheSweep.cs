using Dapper;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Reclaims expired rows from the <c>cache_entries</c> table. Complements the lazy read-time expiry
/// in <see cref="SqlServerCacheStore"/>: lazy expiry hides expired rows from reads, the sweep deletes
/// them so the table and its <c>expires_at</c> index do not bloat.
/// Correctness-neutral — swept rows were already invisible to readers.
/// </summary>
public static class CacheSweep
{
    /// <summary>
    /// Deletes every <c>cache_entries</c> row whose <c>expires_at</c> is at or before the current
    /// database time, across all tenants, and returns the number of rows reclaimed.
    /// </summary>
    public static async Task<int> RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM custodex.cache_entries WHERE expires_at <= SYSUTCDATETIME()", cancellationToken: ct));
    }
}
