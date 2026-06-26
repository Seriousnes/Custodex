using Custodex.Abstractions;

using Dapper;

using Npgsql;

using NpgsqlTypes;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Dapper-backed implementation of <see cref="IChangeLogStore"/> over Postgres.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Every query hard-filters on both <c>store_id</c> and <c>tenant_id</c>.
/// The database generates <c>id</c> (bigserial) and <c>occurred_at</c> (DEFAULT now()) on insert;
/// any values in <see cref="ChangeLogEntry"/> for those fields are ignored.
/// </summary>
public sealed class NpgsqlChangeLogStore(string connectionString) : IChangeLogStore
{
    private readonly string _cs = CustodexSchema.Apply(connectionString);

    private sealed record Row(
        long Id, string Actor, string Operation, string Target,
        string? Before, string? After, DateTime OccurredAt);

    /// <inheritdoc />
    public async Task AppendAsync(
        TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO custodex.change_log (store_id, tenant_id, actor, operation, target, before, after)
            VALUES (@store, @tenant, @actor, @operation, @target, @before, @after)
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("actor", entry.Actor);
        cmd.Parameters.AddWithValue("operation", entry.Operation);
        cmd.Parameters.AddWithValue("target", entry.Target);
        cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Jsonb)
            { Value = entry.Before is null ? DBNull.Value : Json.Serialize(entry.Before) });
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb)
            { Value = entry.After is null ? DBNull.Value : Json.Serialize(entry.After) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(
        TenantContext t, ChangeLogFilter filter, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT id, actor, operation, target, before::text AS before, after::text AS after, occurred_at
            FROM custodex.change_log
            WHERE store_id = @store AND tenant_id = @tenant
              AND (@since IS NULL OR occurred_at >= @since)
              AND (@actor IS NULL OR actor = @actor)
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """,
            new { store = t.Store, tenant = t.Tenant, since = filter.Since, actor = filter.Actor, limit = filter.Limit },
            cancellationToken: ct));

        return [.. rows.Select(r => new ChangeLogEntry(
            r.Id, r.Actor, r.Operation, r.Target,
            Json.Deserialize<object?>(r.Before), Json.Deserialize<object?>(r.After),
            new DateTimeOffset(r.OccurredAt, TimeSpan.Zero)))];
    }
}
