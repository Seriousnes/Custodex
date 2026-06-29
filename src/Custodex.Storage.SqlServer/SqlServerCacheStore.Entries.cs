using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

public sealed partial class SqlServerCacheStore
{
    private sealed record EntryRow(byte[] Value, long Epoch);

    /// <inheritdoc />
    public async Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition("""
            SELECT value, epoch FROM custodex.cache_entries
            WHERE store_id = @store AND tenant_id = @tenant AND cache_key = @key
              AND expires_at > SYSUTCDATETIME()
            """,
            new { store = _scope.Store, tenant = _scope.Tenant, key }, cancellationToken: ct));

        return row is null ? null : new CacheEntry(row.Value, row.Epoch);
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default)
    {
        var ttlSeconds = (int)Math.Clamp(Math.Ceiling(ttl.TotalSeconds), 1, int.MaxValue);
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE custodex.cache_entries
               SET value = @value, epoch = @epoch, expires_at = DATEADD(SECOND, @ttl, SYSUTCDATETIME())
            WHERE store_id = @store AND tenant_id = @tenant AND cache_key = @key;
            IF @@ROWCOUNT = 0
                INSERT INTO custodex.cache_entries (store_id, tenant_id, cache_key, value, epoch, expires_at)
                VALUES (@store, @tenant, @key, @value, @epoch, DATEADD(SECOND, @ttl, SYSUTCDATETIME()));
            """,
            new { store = _scope.Store, tenant = _scope.Tenant, key, value = entry.Value, epoch = entry.Epoch, ttl = ttlSeconds },
            cancellationToken: ct));
    }
}
