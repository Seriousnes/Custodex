using Custodex.Abstractions;

using Dapper;

using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Postgres-backed implementation of <see cref="ICacheStore"/>.
/// Reads epoch values from <c>tenant_epochs</c> with a short-lived connection.
/// Writes (epoch bumps) execute through the <see cref="IUnitOfWork"/> supplied by the caller,
/// so epoch changes commit or roll back with the surrounding data write.
/// Cache entries are persisted to <c>cache_entries</c> for the fixed <see cref="TenantContext"/> scope.
/// </summary>
/// <remarks>Initializes a new <see cref="PostgresCacheStore"/> for the given store/tenant scope.</remarks>
public sealed partial class PostgresCacheStore(string connectionString, TenantContext scope) : ICacheStore
{
    private readonly string _connectionString = CustodexSchema.Apply(connectionString);
    private readonly TenantContext _scope = scope;

    /// <inheritdoc />
    public async Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COALESCE(
                (SELECT epoch FROM custodex.tenant_epochs WHERE store_id = @store AND tenant_id = @tenant), 0)
            """,
            new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO custodex.tenant_epochs (store_id, tenant_id, epoch)
            VALUES (@store, @tenant, 1)
            ON CONFLICT (store_id, tenant_id)
            DO UPDATE SET epoch = tenant_epochs.epoch + 1
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

}
