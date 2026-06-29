using System.Globalization;

using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

/// <summary>
/// Dapper-backed implementation of <see cref="IChangeLogStore"/> over SQLite.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Every query hard-filters on both <c>store_id</c> and <c>tenant_id</c>.
/// The database generates <c>id</c> (rowid alias) and <c>occurred_at</c> (DEFAULT) on insert;
/// any values in <see cref="ChangeLogEntry"/> for those fields are ignored.
/// </summary>
public sealed class SqliteChangeLogStore(string connectionString) : IChangeLogStore
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private readonly string _cs = connectionString;

    private sealed record Row(
        long Id, string Actor, string Operation, string Target,
        string? Before, string? After, string OccurredAt);

    /// <inheritdoc />
    public async Task AppendAsync(
        TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = SqliteUnitOfWork.From(uow);
        await using var cmd = new SqliteCommand("""
            INSERT INTO change_log (store_id, tenant_id, actor, operation, target, before, after)
            VALUES (@store, @tenant, @actor, @operation, @target, @before, @after)
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("@store", t.Store);
        cmd.Parameters.AddWithValue("@tenant", t.Tenant);
        cmd.Parameters.AddWithValue("@actor", entry.Actor);
        cmd.Parameters.AddWithValue("@operation", entry.Operation);
        cmd.Parameters.AddWithValue("@target", entry.Target);
        cmd.Parameters.AddWithValue("@before", entry.Before is null ? DBNull.Value : Json.Serialize(entry.Before));
        cmd.Parameters.AddWithValue("@after", entry.After is null ? DBNull.Value : Json.Serialize(entry.After));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(
        TenantContext t, ChangeLogFilter filter, CancellationToken ct = default)
    {
        await using var conn = await SqliteConnections.OpenAsync(_cs, ct);
        var since = filter.Since?.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT id, actor, operation, target, before, after, occurred_at
            FROM change_log
            WHERE store_id = @store AND tenant_id = @tenant
              AND (@since IS NULL OR occurred_at >= @since)
              AND (@actor IS NULL OR actor = @actor)
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """,
            new { store = t.Store, tenant = t.Tenant, since, actor = filter.Actor, limit = filter.Limit },
            cancellationToken: ct));

        return [.. rows.Select(r => new ChangeLogEntry(
            r.Id, r.Actor, r.Operation, r.Target,
            Json.Deserialize<object?>(r.Before), Json.Deserialize<object?>(r.After),
            ParseTimestamp(r.OccurredAt)))];
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
