using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

/// <summary>
/// Dapper-backed implementation of <see cref="IRelationStore"/> over SQLite.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Every query hard-filters on both <c>store_id</c> and <c>tenant_id</c>.
/// </summary>
public sealed class SqliteRelationStore : IRelationStore
{
    private sealed record Row(
        string ObjectType, string ObjectId, string Relation,
        string SubjectType, string SubjectId, string? SubjectRelation,
        string? ConditionName, string? ConditionParams);

    private const string SelectColumns =
        "object_type, object_id, relation, subject_type, subject_id, subject_relation, " +
        "condition_name, condition_params";

    private readonly string _connectionString;
    private readonly SqliteUnitOfWork? _bound;

    /// <summary>Creates a relation store that opens connections from the given SQLite connection string.</summary>
    /// <param name="connectionString">The SQLite connection string the store reads and writes through.</param>
    public SqliteRelationStore(string connectionString) => _connectionString = connectionString;

    private SqliteRelationStore(string connectionString, SqliteUnitOfWork bound)
    {
        _connectionString = connectionString;
        _bound = bound;
    }

    /// <summary>
    /// Returns a relation store whose reads execute on the given unit of work's connection and
    /// transaction, so they observe writes made earlier on that same uncommitted unit of work.
    /// Writes are unaffected. The returned store does not own the connection and never disposes it.
    /// </summary>
    public SqliteRelationStore OnUnitOfWork(IUnitOfWork uow) => new(_connectionString, SqliteUnitOfWork.From(uow));

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(
        TenantContext t, EntityRef obj, string relation, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = @ot AND object_id = @oid AND relation = @rel
            """;
        return await QueryTuplesAsync(sql,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id, rel = relation },
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(
        TenantContext t, SubjectRef subject, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND subject_type = @st AND subject_id = @sid
              AND COALESCE(subject_relation, '') = COALESCE(@srel, '')
            """;
        return await QueryTuplesAsync(sql,
            new { store = t.Store, tenant = t.Tenant, st = subject.Type, sid = subject.Id, srel = subject.Relation },
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListObjectIdsAsync(
        TenantContext t, string objectType, CancellationToken ct = default)
    {
        const string sql = """
            SELECT DISTINCT object_id FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant AND object_type = @ot
            ORDER BY object_id
            """;
        return await QueryStringsAsync(sql,
            new { store = t.Store, tenant = t.Tenant, ot = objectType },
            ct);
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = SqliteUnitOfWork.From(uow);

        foreach (var tuple in remove)
        {
            await using var cmd = new SqliteCommand("""
                DELETE FROM relation_tuples
                WHERE store_id = @store AND tenant_id = @tenant
                  AND object_type = @ot AND object_id = @oid AND relation = @rel
                  AND subject_type = @st AND subject_id = @sid
                  AND COALESCE(subject_relation, '') = COALESCE(@srel, '')
                """, w.Connection, w.Transaction);
            AddKeyParams(cmd, t, tuple);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var tuple in add)
        {
            await using var cmd = new SqliteCommand("""
                INSERT INTO relation_tuples
                    (store_id, tenant_id, object_type, object_id, relation,
                     subject_type, subject_id, subject_relation, condition_name, condition_params)
                VALUES (@store, @tenant, @ot, @oid, @rel, @st, @sid, @srel, @cname, @cparams)
                ON CONFLICT (store_id, tenant_id, object_type, object_id, relation,
                             subject_type, subject_id, COALESCE(subject_relation, ''))
                DO UPDATE SET condition_name = excluded.condition_name,
                              condition_params = excluded.condition_params
                """, w.Connection, w.Transaction);
            AddKeyParams(cmd, t, tuple);
            cmd.Parameters.AddWithValue("@cname", (object?)tuple.Condition?.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cparams",
                tuple.Condition is null ? DBNull.Value : Json.Serialize(tuple.Condition.Parameters));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Admin/management read: returns tuples matching the given filter, scoped to <paramref name="t"/>.
    /// Each filter field is optional; omitting it means "match any value" for that column.
    /// </summary>
    public async Task<IReadOnlyList<RelationTuple>> QueryAsync(
        TenantContext t, TupleFilter filter, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND (@ot IS NULL OR object_type = @ot)
              AND (@oid IS NULL OR object_id = @oid)
              AND (@rel IS NULL OR relation = @rel)
              AND (@st IS NULL OR subject_type = @st)
              AND (@sid IS NULL OR subject_id = @sid)
            """;
        return await QueryTuplesAsync(sql,
            new
            {
                store = t.Store, tenant = t.Tenant,
                ot = filter.ObjectType, oid = filter.ObjectId, rel = filter.Relation,
                st = filter.SubjectType, sid = filter.SubjectId,
            },
            ct);
    }

    private async Task<IReadOnlyList<RelationTuple>> QueryTuplesAsync(string sql, object args, CancellationToken ct)
    {
        if (_bound is { } b)
        {
            var rows = await b.Connection.QueryAsync<Row>(new CommandDefinition(sql, args, transaction: b.Transaction, cancellationToken: ct));
            return [.. rows.Select(Map)];
        }
        await using var conn = await SqliteConnections.OpenAsync(_connectionString, ct);
        var ownRows = await conn.QueryAsync<Row>(new CommandDefinition(sql, args, cancellationToken: ct));
        return [.. ownRows.Select(Map)];
    }

    private async Task<IReadOnlyList<string>> QueryStringsAsync(string sql, object args, CancellationToken ct)
    {
        if (_bound is { } b)
        {
            var rows = await b.Connection.QueryAsync<string>(new CommandDefinition(sql, args, transaction: b.Transaction, cancellationToken: ct));
            return [.. rows];
        }
        await using var conn = await SqliteConnections.OpenAsync(_connectionString, ct);
        var ownRows = await conn.QueryAsync<string>(new CommandDefinition(sql, args, cancellationToken: ct));
        return [.. ownRows];
    }

    private static void AddKeyParams(SqliteCommand cmd, TenantContext t, RelationTuple tuple)
    {
        cmd.Parameters.AddWithValue("@store", t.Store);
        cmd.Parameters.AddWithValue("@tenant", t.Tenant);
        cmd.Parameters.AddWithValue("@ot", tuple.Object.Type);
        cmd.Parameters.AddWithValue("@oid", tuple.Object.Id);
        cmd.Parameters.AddWithValue("@rel", tuple.Relation);
        cmd.Parameters.AddWithValue("@st", tuple.Subject.Type);
        cmd.Parameters.AddWithValue("@sid", tuple.Subject.Id);
        cmd.Parameters.AddWithValue("@srel", (object?)tuple.Subject.Relation ?? DBNull.Value);
    }

    private static RelationTuple Map(Row r)
    {
        ConditionRef? condition = r.ConditionName is null
            ? null
            : new ConditionRef(r.ConditionName,
                Json.Deserialize<Dictionary<string, object?>>(r.ConditionParams)
                    ?? []);

        return new RelationTuple(
            new EntityRef(r.ObjectType, r.ObjectId),
            r.Relation,
            new SubjectRef(r.SubjectType, r.SubjectId, r.SubjectRelation),
            condition);
    }
}
