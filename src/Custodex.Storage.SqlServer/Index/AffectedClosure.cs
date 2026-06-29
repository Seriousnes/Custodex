using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer.Index;

/// <summary>
/// Computes the set of objects whose reverse-index rows could change as a consequence
/// of a write (the added union removed tuples). Returns a complete superset: recomputing
/// an unchanged object is harmless; missing a changed one is a correctness bug. Climbs
/// inbound structural-arrow edges and group-membership edges transitively from the changed
/// objects, reading on the write transaction so the closure reflects post-write state.
/// Hard-filters on (store, tenant). A T-SQL recursive CTE is <c>UNION ALL</c> only, so an
/// accumulated visited-path with a <c>CHARINDEX</c> guard terminates the climb on cyclic data.
/// </summary>
public static class AffectedClosure
{
    private const string InboundSql = """
        WITH reached (otype, oid, visited) AS (
            SELECT
                CAST(@ot AS nvarchar(128)) COLLATE Latin1_General_100_BIN2,
                CAST(@oid AS nvarchar(128)) COLLATE Latin1_General_100_BIN2,
                CAST(NCHAR(2) + @ot + NCHAR(1) + @oid + NCHAR(3) AS nvarchar(max))
                    COLLATE Latin1_General_100_BIN2
          UNION ALL
            SELECT
                rt.object_type, rt.object_id,
                r.visited + NCHAR(2) + rt.object_type + NCHAR(1) + rt.object_id + NCHAR(3)
            FROM reached r
            JOIN custodex.relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = r.otype AND rt.subject_id = r.oid
            WHERE CHARINDEX(NCHAR(2) + rt.object_type + NCHAR(1) + rt.object_id + NCHAR(3), r.visited) = 0
        )
        SELECT DISTINCT otype AS Otype, oid AS Oid FROM reached
        OPTION (MAXRECURSION 0)
        """;

    /// <summary>
    /// Returns the distinct <see cref="EntityRef"/>s whose reverse-index rows could be
    /// affected by <paramref name="changed"/>. Seeds from each changed tuple's object then
    /// climbs inbound edges transitively using a recursive CTE executed on
    /// <paramref name="tx"/> so it reads post-write state.
    /// </summary>
    public static async Task<IReadOnlyList<EntityRef>> ComputeAsync(
        SqlConnection conn, SqlTransaction? tx, TenantContext t,
        IReadOnlyList<RelationTuple> changed, CancellationToken ct = default)
    {
        if (changed.Count == 0) return [];

        var seeds = new HashSet<EntityRef>();
        foreach (var tuple in changed) seeds.Add(tuple.Object);

        var result = new HashSet<EntityRef>();
        foreach (var seed in seeds)
        {
            var rows = await conn.QueryAsync<Row>(new CommandDefinition(InboundSql,
                new { store = t.Store, tenant = t.Tenant, ot = seed.Type, oid = seed.Id },
                transaction: tx, cancellationToken: ct));
            foreach (var r in rows) result.Add(new EntityRef(r.Otype, r.Oid));
        }

        return [.. result];
    }

    private sealed record Row(string Otype, string Oid);
}
