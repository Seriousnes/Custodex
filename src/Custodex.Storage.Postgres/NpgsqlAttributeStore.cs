using Custodex.Abstractions;

using Dapper;

using Npgsql;

using NpgsqlTypes;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Dapper-backed implementation of <see cref="IAttributeStore"/> over Postgres.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Every query hard-filters on both <c>store_id</c> and <c>tenant_id</c>.
/// </summary>
public sealed class NpgsqlAttributeStore(string connectionString) : IAttributeStore
{
    private readonly string _cs = CustodexSchema.Apply(connectionString);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        TenantContext t, EntityRef obj, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT attributes::text FROM custodex.object_attributes
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = @ot AND object_id = @oid
            """,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id },
            cancellationToken: ct));

        return json is null ? null : Json.DeserializeValues(json);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO custodex.object_attributes (store_id, tenant_id, object_type, object_id, attributes)
            VALUES (@store, @tenant, @ot, @oid, @attrs)
            ON CONFLICT (store_id, tenant_id, object_type, object_id)
            DO UPDATE SET attributes = EXCLUDED.attributes
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("ot", obj.Type);
        cmd.Parameters.AddWithValue("oid", obj.Id);
        cmd.Parameters.Add(new NpgsqlParameter("attrs", NpgsqlDbType.Jsonb) { Value = Json.Serialize(attrs) });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
