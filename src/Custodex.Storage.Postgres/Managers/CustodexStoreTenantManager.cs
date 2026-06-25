using Dapper;
using Npgsql;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres.Managers;

/// <summary>
/// Concrete <see cref="IStoreManager"/> backed by Postgres.
/// Inserts a row into the <c>stores</c> table; a duplicate store id is silently ignored.
/// </summary>
public sealed class CustodexStoreManager(string connectionString) : IStoreManager
{
    private readonly string _cs = CustodexSchema.Apply(connectionString);

    /// <inheritdoc />
    public async Task CreateStoreAsync(string store, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO stores (id) VALUES (@store) ON CONFLICT DO NOTHING",
            new { store }, cancellationToken: ct));
    }
}

/// <summary>
/// Concrete <see cref="ITenantManager"/> backed by Postgres.
/// Inserts a row into the <c>tenants</c> table; a duplicate store/tenant pair is silently ignored.
/// </summary>
public sealed class CustodexTenantManager(string connectionString) : ITenantManager
{
    private readonly string _cs = CustodexSchema.Apply(connectionString);

    /// <inheritdoc />
    public async Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@store, @tenant) ON CONFLICT DO NOTHING",
            new { store = tenant.Store, tenant = tenant.Tenant }, cancellationToken: ct));
    }
}
