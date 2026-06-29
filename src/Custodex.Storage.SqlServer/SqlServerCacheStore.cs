using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// SQL Server-backed implementation of <see cref="ICacheStore"/>.
/// Reads epoch values from <c>tenant_epochs</c> with a short-lived connection.
/// Writes (epoch bumps) execute through the <see cref="IUnitOfWork"/> supplied by the caller,
/// so epoch changes commit or roll back with the surrounding data write.
/// Cache entries are persisted to <c>cache_entries</c> for the fixed <see cref="TenantContext"/> scope.
/// </summary>
/// <remarks>Initializes a new <see cref="SqlServerCacheStore"/> for the given store/tenant scope.</remarks>
public sealed partial class SqlServerCacheStore(string connectionString, TenantContext scope) : ICacheStore
{
    private readonly string _connectionString = connectionString;
    private readonly TenantContext _scope = scope;

    /// <inheritdoc />
    public async Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COALESCE(
                (SELECT epoch FROM custodex.tenant_epochs WHERE store_id = @store AND tenant_id = @tenant), 0)
            """,
            new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = SqlServerUnitOfWork.From(uow);
        await using var cmd = new SqlCommand("""
            UPDATE custodex.tenant_epochs SET epoch = epoch + 1
            WHERE store_id = @store AND tenant_id = @tenant;
            IF @@ROWCOUNT = 0
                INSERT INTO custodex.tenant_epochs (store_id, tenant_id, epoch)
                VALUES (@store, @tenant, 1);
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("@store", t.Store);
        cmd.Parameters.AddWithValue("@tenant", t.Tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
