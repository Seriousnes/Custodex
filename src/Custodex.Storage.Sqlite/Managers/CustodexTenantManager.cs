using Custodex.Abstractions;

using Dapper;

namespace Custodex.Storage.Sqlite.Managers;

/// <summary>
/// Concrete <see cref="ITenantManager"/> backed by SQLite.
/// Inserts a row into the <c>tenants</c> table; a duplicate store/tenant pair is silently ignored.
/// </summary>
public sealed class CustodexTenantManager(string connectionString) : ITenantManager
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
    {
        await using var conn = await SqliteConnections.OpenAsync(_cs, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@store, @tenant) ON CONFLICT DO NOTHING",
            new { store = tenant.Store, tenant = tenant.Tenant }, cancellationToken: ct));
    }
}
