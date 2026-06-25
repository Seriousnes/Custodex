using Dapper;
using Npgsql;

namespace Custodex.Storage.Postgres.Tests.Spike;

/// <summary>
/// Candidate recursive-CTE reachability primitive (validated by this spike and the differential harness).
/// Given one (object, relation), returns the distinct leaf subjects reachable by transitively
/// expanding subject-set (group#member-style) tuples. Leaf rows are subjects with NULL
/// subject_relation (concrete users and wildcards). Subject-sets are expanded, never returned.
/// </summary>
internal static class ReachabilityCte
{
    private sealed record Leaf(string SubjectType, string SubjectId);

    private const string Sql = """
        WITH RECURSIVE reach (object_type, object_id, relation) AS (
            -- base: the requested (object, relation)
            SELECT @ot::text COLLATE "C", @oid::text COLLATE "C", @rel::text COLLATE "C"
          UNION
            -- step: for each frontier tuple whose subject is a subject-set group:G#srel,
            -- follow into G's srel tuples (one more hop of nested membership).
            SELECT rt.subject_type, rt.subject_id, rt.subject_relation
            FROM reach r
            JOIN relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.object_type = r.object_type
             AND rt.object_id   = r.object_id
             AND rt.relation    = r.relation
            WHERE rt.subject_relation IS NOT NULL
        )
        SELECT DISTINCT rt.subject_type AS SubjectType, rt.subject_id AS SubjectId
        FROM reach r
        JOIN relation_tuples rt
          ON rt.store_id = @store AND rt.tenant_id = @tenant
         AND rt.object_type = r.object_type
         AND rt.object_id   = r.object_id
         AND rt.relation    = r.relation
        WHERE rt.subject_relation IS NULL          -- leaf subjects only
        """;

    internal static async Task<IReadOnlyList<(string Type, string Id)>> SubjectsThroughRelationAsync(
        NpgsqlConnection conn, string store, string tenant,
        string objectType, string objectId, string relation, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<Leaf>(new CommandDefinition(Sql,
            new { store, tenant, ot = objectType, oid = objectId, rel = relation },
            cancellationToken: ct));
        return rows.Select(l => (l.SubjectType, l.SubjectId)).ToList();
    }
}
