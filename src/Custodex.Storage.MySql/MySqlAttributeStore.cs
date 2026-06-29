using Custodex.Abstractions;

using Dapper;

using MySqlConnector;

namespace Custodex.Storage.MySql;

/// <summary>
/// Dapper-backed implementation of <see cref="IAttributeStore"/> over MySQL.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Every query hard-filters on both <c>store_id</c> and <c>tenant_id</c>.
/// Values round-trip through native CLR types, so a stored integer reads back as a
/// <see cref="long"/> for condition evaluation.
/// </summary>
public sealed class MySqlAttributeStore(string connectionString) : IAttributeStore
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        TenantContext t, EntityRef obj, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_cs);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT attributes FROM object_attributes
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = @ot AND object_id = @oid
            """,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id },
            cancellationToken: ct));

        return json is null ? null : Json.Deserialize<Dictionary<string, object?>>(json);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = MySqlUnitOfWork.From(uow);
        await using var cmd = new MySqlCommand("""
            INSERT INTO object_attributes (store_id, tenant_id, object_type, object_id, attributes)
            VALUES (@store, @tenant, @ot, @oid, @attrs)
            ON DUPLICATE KEY UPDATE attributes = VALUES(attributes)
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("ot", obj.Type);
        cmd.Parameters.AddWithValue("oid", obj.Id);
        cmd.Parameters.AddWithValue("attrs", Json.Serialize(attrs));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
