using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="ICacheStore"/>.
/// Reads epoch values from <c>tenant_epochs</c> with a short-lived connection.
/// Writes (epoch bumps) execute through the <see cref="IUnitOfWork"/> supplied by the caller, so
/// epoch changes commit or roll back with the surrounding data write. Cache entries are persisted to
/// <c>cache_entries</c> for the fixed <see cref="TenantContext"/> scope.
/// </summary>
/// <remarks>Initializes a new <see cref="SqliteCacheStore"/> for the given store/tenant scope.</remarks>
public sealed class SqliteCacheStore(string connectionString, TenantContext scope) : ICacheStore
{
    private readonly string _connectionString = connectionString;
    private readonly TenantContext _scope = scope;

    private sealed record EntryRow(byte[] Value, long Epoch);

    /// <inheritdoc />
    public async Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default)
    {
        await using var conn = await SqliteConnections.OpenAsync(_connectionString, ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COALESCE(
                (SELECT epoch FROM tenant_epochs WHERE store_id = @store AND tenant_id = @tenant), 0)
            """,
            new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = SqliteUnitOfWork.From(uow);
        await using var cmd = new SqliteCommand("""
            INSERT INTO tenant_epochs (store_id, tenant_id, epoch)
            VALUES (@store, @tenant, 1)
            ON CONFLICT (store_id, tenant_id)
            DO UPDATE SET epoch = epoch + 1
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("@store", t.Store);
        cmd.Parameters.AddWithValue("@tenant", t.Tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await SqliteConnections.OpenAsync(_connectionString, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = await conn.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition("""
            SELECT value, epoch FROM cache_entries
            WHERE store_id = @store AND tenant_id = @tenant AND key = @key
              AND expires_at > @now
            """,
            new { store = _scope.Store, tenant = _scope.Tenant, key, now }, cancellationToken: ct));

        return row is null ? null : new CacheEntry(row.Value, row.Epoch);
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var conn = await SqliteConnections.OpenAsync(_connectionString, ct);
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO cache_entries (store_id, tenant_id, key, value, epoch, expires_at)
            VALUES (@store, @tenant, @key, @value, @epoch, @expiresAt)
            ON CONFLICT (store_id, tenant_id, key)
            DO UPDATE SET value = excluded.value, epoch = excluded.epoch, expires_at = excluded.expires_at
            """,
            new { store = _scope.Store, tenant = _scope.Tenant, key, value = entry.Value, epoch = entry.Epoch, expiresAt },
            cancellationToken: ct));
    }
}
