using Custodex.Abstractions;

using Dapper;

using MySqlConnector;

namespace Custodex.Storage.MySql;

/// <summary>
/// MySQL-backed implementation of <see cref="ICacheStore"/>.
/// Reads epoch values from <c>tenant_epochs</c> with a short-lived connection.
/// Writes (epoch bumps) execute through the <see cref="IUnitOfWork"/> supplied by the caller,
/// so epoch changes commit or roll back with the surrounding data write.
/// Cache entries are persisted to <c>cache_entries</c> for the fixed <see cref="TenantContext"/> scope.
/// </summary>
public sealed class MySqlCacheStore(string connectionString, TenantContext scope) : ICacheStore
{
    private readonly string _connectionString = connectionString;
    private readonly TenantContext _scope = scope;

    private sealed record EntryRow(byte[] Value, long Epoch);

    /// <inheritdoc />
    public async Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COALESCE(
                (SELECT epoch FROM tenant_epochs WHERE store_id = @store AND tenant_id = @tenant), 0)
            """,
            new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<long> BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = MySqlUnitOfWork.From(uow);
        await using (var cmd = new MySqlCommand("""
            INSERT INTO tenant_epochs (store_id, tenant_id, epoch)
            VALUES (@store, @tenant, 1)
            ON DUPLICATE KEY UPDATE epoch = epoch + 1
            """, w.Connection, w.Transaction))
        {
            cmd.Parameters.AddWithValue("store", t.Store);
            cmd.Parameters.AddWithValue("tenant", t.Tenant);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var read = new MySqlCommand("""
            SELECT epoch FROM tenant_epochs WHERE store_id = @store AND tenant_id = @tenant
            """, w.Connection, w.Transaction);
        read.Parameters.AddWithValue("store", t.Store);
        read.Parameters.AddWithValue("tenant", t.Tenant);
        return Convert.ToInt64(await read.ExecuteScalarAsync(ct));
    }

    /// <inheritdoc />
    public async Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition("""
            SELECT value, epoch FROM cache_entries
            WHERE store_id = @store AND tenant_id = @tenant AND `key` = @key
              AND expires_at > NOW(6)
            """,
            new { store = _scope.Store, tenant = _scope.Tenant, key }, cancellationToken: ct));

        return row is null ? null : new CacheEntry(row.Value, row.Epoch);
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cache_entries (store_id, tenant_id, `key`, value, epoch, expires_at)
            VALUES (@store, @tenant, @key, @value, @epoch, DATE_ADD(NOW(6), INTERVAL @ttlMicros MICROSECOND))
            ON DUPLICATE KEY UPDATE value = VALUES(value), epoch = VALUES(epoch), expires_at = VALUES(expires_at)
            """,
            new
            {
                store = _scope.Store, tenant = _scope.Tenant, key,
                value = entry.Value, epoch = entry.Epoch, ttlMicros = (long)(ttl.TotalMilliseconds * 1000),
            },
            cancellationToken: ct));
    }
}
