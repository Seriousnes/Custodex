using Dapper;
using Npgsql;
using NpgsqlTypes;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Dapper-backed implementation of <see cref="IRelationStore"/> over Postgres.
/// Reads open short-lived connections from the supplied connection string.
/// Writes execute through the <see cref="IUnitOfWork"/> supplied by the caller.
/// Every query hard-filters on both <c>store_id</c> and <c>tenant_id</c>.
/// </summary>
public sealed class NpgsqlRelationStore(string connectionString) : IRelationStore
{
    private sealed record Row(
        string ObjectType, string ObjectId, string Relation,
        string SubjectType, string SubjectId, string? SubjectRelation,
        string? ConditionName, string? ConditionParams);

    private const string SelectColumns =
        "object_type, object_id, relation, subject_type, subject_id, subject_relation, " +
        "condition_name, condition_params::text AS condition_params";

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(
        TenantContext t, EntityRef obj, string relation, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = @ot AND object_id = @oid AND relation = @rel
            """,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id, rel = relation },
            cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(
        TenantContext t, SubjectRef subject, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND subject_type = @st AND subject_id = @sid
              AND COALESCE(subject_relation, '') = COALESCE(@srel, '')
            """,
            new { store = t.Store, tenant = t.Tenant, st = subject.Type, sid = subject.Id, srel = subject.Relation },
            cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListObjectIdsAsync(
        TenantContext t, string objectType, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var ids = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT object_id FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant AND object_type = @ot
            """,
            new { store = t.Store, tenant = t.Tenant, ot = objectType },
            cancellationToken: ct));
        return ids.ToList();
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);

        foreach (var tuple in remove)
        {
            await using var cmd = new NpgsqlCommand("""
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
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO relation_tuples
                    (store_id, tenant_id, object_type, object_id, relation,
                     subject_type, subject_id, subject_relation, condition_name, condition_params)
                VALUES (@store, @tenant, @ot, @oid, @rel, @st, @sid, @srel, @cname, @cparams)
                ON CONFLICT (store_id, tenant_id, object_type, object_id, relation,
                             subject_type, subject_id, COALESCE(subject_relation, ''))
                DO UPDATE SET condition_name = EXCLUDED.condition_name,
                              condition_params = EXCLUDED.condition_params
                """, w.Connection, w.Transaction);
            AddKeyParams(cmd, t, tuple);
            cmd.Parameters.AddWithValue("cname", (object?)tuple.Condition?.Name ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("cparams", NpgsqlDbType.Jsonb)
            {
                Value = tuple.Condition is null ? DBNull.Value : Json.Serialize(tuple.Condition.Parameters)
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void AddKeyParams(NpgsqlCommand cmd, TenantContext t, RelationTuple tuple)
    {
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("ot", tuple.Object.Type);
        cmd.Parameters.AddWithValue("oid", tuple.Object.Id);
        cmd.Parameters.AddWithValue("rel", tuple.Relation);
        cmd.Parameters.AddWithValue("st", tuple.Subject.Type);
        cmd.Parameters.AddWithValue("sid", tuple.Subject.Id);
        cmd.Parameters.AddWithValue("srel", (object?)tuple.Subject.Relation ?? DBNull.Value);
    }

    private static RelationTuple Map(Row r)
    {
        ConditionRef? condition = r.ConditionName is null
            ? null
            : new ConditionRef(r.ConditionName,
                Json.Deserialize<Dictionary<string, object?>>(r.ConditionParams)
                    ?? new Dictionary<string, object?>());

        return new RelationTuple(
            new EntityRef(r.ObjectType, r.ObjectId),
            r.Relation,
            new SubjectRef(r.SubjectType, r.SubjectId, r.SubjectRelation),
            condition);
    }
}
