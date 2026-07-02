using Custodex.Abstractions;

using Dapper;

using MySqlConnector;

namespace Custodex.Storage.MySql;

/// <summary>
/// Dapper-backed implementation of <see cref="IChangeLogStore"/> over MySQL.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Every query hard-filters on both <c>store_id</c> and <c>tenant_id</c>.
/// The database generates <c>id</c> (auto-increment) and <c>occurred_at</c> on insert;
/// any values in <see cref="ChangeLogEntry"/> for those fields are ignored.
/// </summary>
public sealed class MySqlChangeLogStore(string connectionString) : IChangeLogStore
{
    private readonly string _cs = connectionString;

    private sealed record Row(
        long Id, string Actor, string Operation, string Target,
        string? Before, string? After, DateTime OccurredAt);

    /// <inheritdoc />
    public async Task<long> AppendAsync(
        TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = MySqlUnitOfWork.From(uow);
        await using var cmd = new MySqlCommand("""
            INSERT INTO change_log (store_id, tenant_id, actor, operation, target, `before`, `after`)
            VALUES (@store, @tenant, @actor, @operation, @target, @before, @after)
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("actor", entry.Actor);
        cmd.Parameters.AddWithValue("operation", entry.Operation);
        cmd.Parameters.AddWithValue("target", entry.Target);
        cmd.Parameters.AddWithValue("before", entry.Before is null ? DBNull.Value : Json.Serialize(entry.Before));
        cmd.Parameters.AddWithValue("after", entry.After is null ? DBNull.Value : Json.Serialize(entry.After));
        await cmd.ExecuteNonQueryAsync(ct);
        return cmd.LastInsertedId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(
        TenantContext t, ChangeLogFilter filter, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_cs);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT id, actor, operation, target, `before`, `after`, occurred_at
            FROM change_log
            WHERE store_id = @store AND tenant_id = @tenant
              AND (@since IS NULL OR occurred_at >= @since)
              AND (@actor IS NULL OR actor = @actor)
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """,
            new
            {
                store = t.Store, tenant = t.Tenant,
                since = filter.Since?.UtcDateTime, actor = filter.Actor, limit = filter.Limit,
            },
            cancellationToken: ct));

        return [.. rows.Select(r => new ChangeLogEntry(
            r.Id, r.Actor, r.Operation, r.Target,
            Json.Deserialize<object?>(r.Before), Json.Deserialize<object?>(r.After),
            new DateTimeOffset(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc), TimeSpan.Zero)))];
    }
}
