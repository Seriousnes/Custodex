using Dapper;
using Npgsql;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

public sealed partial class PostgresCacheStore
{
    private sealed record EntryRow(byte[] Value, long Epoch);

    /// <inheritdoc />
    public async Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition("""
            SELECT value, epoch FROM custodex.cache_entries
            WHERE store_id = @store AND tenant_id = @tenant AND key = @key
              AND expires_at > now()
            """,
            new { store = _scope.Store, tenant = _scope.Tenant, key }, cancellationToken: ct));

        return row is null ? null : new CacheEntry(row.Value, row.Epoch);
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO custodex.cache_entries (store_id, tenant_id, key, value, epoch, expires_at)
            VALUES (@store, @tenant, @key, @value, @epoch, now() + @ttl)
            ON CONFLICT (store_id, tenant_id, key)
            DO UPDATE SET value = EXCLUDED.value, epoch = EXCLUDED.epoch, expires_at = EXCLUDED.expires_at
            """,
            new { store = _scope.Store, tenant = _scope.Tenant, key, value = entry.Value, epoch = entry.Epoch, ttl },
            cancellationToken: ct));
    }
}
