using Custodex.Abstractions;

using Dapper;

using MySqlConnector;

namespace Custodex.Storage.MySql.Managers;

/// <summary>
/// Concrete <see cref="ITenantManager"/> backed by MySQL.
/// Inserts a row into the <c>tenants</c> table; a duplicate store/tenant pair is silently ignored.
/// </summary>
public sealed class CustodexTenantManager(string connectionString) : ITenantManager
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
    {
        MySqlColumnLimits.ValidateTenant(tenant);
        await using var conn = new MySqlConnection(_cs);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO tenants (store_id, tenant_id) VALUES (@store, @tenant)",
            new { store = tenant.Store, tenant = tenant.Tenant }, cancellationToken: ct));
    }
}
