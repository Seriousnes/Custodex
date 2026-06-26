using Custodex.Abstractions;

using Dapper;

using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Recursive-CTE reachability primitives over <c>relation_tuples</c>.
/// <see cref="SubjectsThroughRelationAsync"/> expands nested subject-sets in SQL and returns the
/// distinct leaf subjects. <see cref="EdgesThroughRelationAsync"/> returns the direct structural
/// tuples for arrow edge-following. Both hard-filter on <c>(store_id, tenant_id)</c>.
/// </summary>
public static class CteReachability
{
    private sealed record LeafRow(
        string SubjectType, string SubjectId, string? ConditionName, string? ConditionParams);

    private const string SubjectsSql = """
        WITH RECURSIVE reach (object_type, object_id, relation) AS (
            SELECT @ot::text COLLATE "C", @oid::text COLLATE "C", @rel::text COLLATE "C"
          UNION
            SELECT rt.subject_type, rt.subject_id, rt.subject_relation
            FROM reach r
            JOIN custodex.relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.object_type = r.object_type
             AND rt.object_id   = r.object_id
             AND rt.relation    = r.relation
            WHERE rt.subject_relation IS NOT NULL
        )
        SELECT DISTINCT
            rt.subject_type AS SubjectType,
            rt.subject_id   AS SubjectId,
            rt.condition_name AS ConditionName,
            rt.condition_params::text AS ConditionParams
        FROM reach r
        JOIN custodex.relation_tuples rt
          ON rt.store_id = @store AND rt.tenant_id = @tenant
         AND rt.object_type = r.object_type
         AND rt.object_id   = r.object_id
         AND rt.relation    = r.relation
        WHERE rt.subject_relation IS NULL
        """;

    private const string EdgesSql = """
        SELECT subject_type AS SubjectType, subject_id AS SubjectId,
               subject_relation AS SubjectRelation,
               condition_name AS ConditionName, condition_params::text AS ConditionParams
        FROM custodex.relation_tuples
        WHERE store_id = @store AND tenant_id = @tenant
          AND object_type = @ot AND object_id = @oid AND relation = @rel
        """;

    /// <summary>
    /// Returns the distinct leaf subjects (concrete and wildcard) reachable through
    /// <paramref name="obj"/>#<paramref name="relation"/>, expanding nested subject-sets in SQL.
    /// Each returned <see cref="SubjectRef"/> carries identity only; subject-set leaves are
    /// expanded away.
    /// </summary>
    public static async Task<IReadOnlyList<SubjectRef>> SubjectsThroughRelationAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, TenantContext t,
        EntityRef obj, string relation, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<LeafRow>(new CommandDefinition(SubjectsSql,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id, rel = relation },
            transaction: tx, cancellationToken: ct));
        return [.. rows.Select(r => new SubjectRef(r.SubjectType, r.SubjectId)).Distinct()];
    }

    /// <summary>
    /// Returns the direct (non-expanded) tuples on <paramref name="obj"/>#<paramref name="relation"/>.
    /// Each tuple's subject is the related object for arrow edge-following, with any carried condition.
    /// </summary>
    public static async Task<IReadOnlyList<RelationTuple>> EdgesThroughRelationAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, TenantContext t,
        EntityRef obj, string relation, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<EdgeRow>(new CommandDefinition(EdgesSql,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id, rel = relation },
            transaction: tx, cancellationToken: ct));
        return [.. rows.Select(r =>
        {
            ConditionRef? condition = r.ConditionName is null
                ? null
                : new ConditionRef(r.ConditionName,
                    Json.Deserialize<Dictionary<string, object?>>(r.ConditionParams) ?? []);
            return new RelationTuple(obj, relation,
                new SubjectRef(r.SubjectType, r.SubjectId, r.SubjectRelation), condition);
        })];
    }

    private sealed record EdgeRow(
        string SubjectType, string SubjectId, string? SubjectRelation,
        string? ConditionName, string? ConditionParams);
}
