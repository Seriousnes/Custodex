using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

/// <summary>
/// Dapper-backed implementation of <see cref="ISchemaStore"/> over SQLite.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Schema is per-store; every query filters on <c>store_id</c>.
/// </summary>
public sealed class SqliteSchemaStore(string connectionString) : ISchemaStore
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default)
    {
        await using var conn = await SqliteConnections.OpenAsync(_cs, ct);
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
        var w = SqliteUnitOfWork.From(uow);

        await using (var deactivate = new SqliteCommand(
            "UPDATE schema_versions SET is_active = 0 WHERE store_id = @store AND is_active = 1",
            w.Connection, w.Transaction))
        {
            deactivate.Parameters.AddWithValue("@store", store);
            await deactivate.ExecuteNonQueryAsync(ct);
        }

        await using var upsert = new SqliteCommand("""
            INSERT INTO schema_versions (store_id, version, definition, is_active)
            VALUES (@store, @version, @definition, 1)
            ON CONFLICT (store_id, version)
            DO UPDATE SET definition = excluded.definition, is_active = 1
            """, w.Connection, w.Transaction);
        upsert.Parameters.AddWithValue("@store", store);
        upsert.Parameters.AddWithValue("@version", schema.Version);
        upsert.Parameters.AddWithValue("@definition", Json.Serialize(schema));
        await upsert.ExecuteNonQueryAsync(ct);
    }
}
