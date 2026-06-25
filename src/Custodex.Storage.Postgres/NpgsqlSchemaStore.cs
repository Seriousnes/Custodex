using Dapper;
using Npgsql;
using NpgsqlTypes;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Dapper-backed implementation of <see cref="ISchemaStore"/> over Postgres.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Schema is per-store; every query filters on <c>store_id</c>.
/// </summary>
public sealed class NpgsqlSchemaStore(string connectionString) : ISchemaStore
{
    private readonly string _cs = CustodexSchema.Apply(connectionString);

    /// <inheritdoc />
    public async Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT definition::text FROM custodex.schema_versions
            WHERE store_id = @store AND is_active
            """,
            new { store }, cancellationToken: ct));

        return json is null ? null : Json.Deserialize<Schema>(json);
    }

    /// <inheritdoc />
    public async Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);

        await using (var deactivate = new NpgsqlCommand(
            "UPDATE custodex.schema_versions SET is_active = false WHERE store_id = @store AND is_active",
            w.Connection, w.Transaction))
        {
            deactivate.Parameters.AddWithValue("store", store);
            await deactivate.ExecuteNonQueryAsync(ct);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO custodex.schema_versions (store_id, version, definition, is_active)
            VALUES (@store, @version, @definition, true)
            ON CONFLICT (store_id, version)
            DO UPDATE SET definition = EXCLUDED.definition, is_active = true
            """, w.Connection, w.Transaction);
        upsert.Parameters.AddWithValue("store", store);
        upsert.Parameters.AddWithValue("version", schema.Version);
        upsert.Parameters.Add(new NpgsqlParameter("definition", NpgsqlDbType.Jsonb) { Value = Json.Serialize(schema) });
        await upsert.ExecuteNonQueryAsync(ct);
    }
}
