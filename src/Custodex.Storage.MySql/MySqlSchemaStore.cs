using Custodex.Abstractions;

using Dapper;

using MySqlConnector;

namespace Custodex.Storage.MySql;

/// <summary>
/// Dapper-backed implementation of <see cref="ISchemaStore"/> over MySQL.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Schema is per-store; every query filters on <c>store_id</c>.
/// </summary>
public sealed class MySqlSchemaStore(string connectionString) : ISchemaStore
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_cs);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT definition FROM schema_versions
            WHERE store_id = @store AND is_active = 1
            """,
            new { store }, cancellationToken: ct));

        return json is null ? null : Json.Deserialize<Schema>(json);
    }

    /// <inheritdoc />
    public async Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = MySqlUnitOfWork.From(uow);

        await using (var deactivate = new MySqlCommand(
            "UPDATE schema_versions SET is_active = 0 WHERE store_id = @store AND is_active = 1",
            w.Connection, w.Transaction))
        {
            deactivate.Parameters.AddWithValue("store", store);
            await deactivate.ExecuteNonQueryAsync(ct);
        }

        await using var upsert = new MySqlCommand("""
            INSERT INTO schema_versions (store_id, version, definition, is_active)
            VALUES (@store, @version, @definition, 1)
            ON DUPLICATE KEY UPDATE definition = VALUES(definition), is_active = 1
            """, w.Connection, w.Transaction);
        upsert.Parameters.AddWithValue("store", store);
        upsert.Parameters.AddWithValue("version", schema.Version);
        upsert.Parameters.AddWithValue("definition", Json.Serialize(schema));
        await upsert.ExecuteNonQueryAsync(ct);
    }
}
