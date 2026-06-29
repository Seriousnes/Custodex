using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer.Managers;

/// <summary>
/// Concrete <see cref="IStoreManager"/> backed by SQL Server.
/// Inserts a row into the <c>stores</c> table; a duplicate store id is silently ignored.
/// </summary>
public sealed class CustodexStoreManager(string connectionString) : IStoreManager
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task CreateStoreAsync(string store, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM custodex.stores WHERE id = @store)
                INSERT INTO custodex.stores (id) VALUES (@store);
            """, new { store }, cancellationToken: ct));
    }
}

/// <summary>
/// Concrete <see cref="ITenantManager"/> backed by SQL Server.
/// Inserts a row into the <c>tenants</c> table; a duplicate store/tenant pair is silently ignored.
/// </summary>
public sealed class CustodexTenantManager(string connectionString) : ITenantManager
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM custodex.tenants WHERE store_id = @store AND tenant_id = @tenant)
                INSERT INTO custodex.tenants (store_id, tenant_id) VALUES (@store, @tenant);
            """, new { store = tenant.Store, tenant = tenant.Tenant }, cancellationToken: ct));
    }
}
