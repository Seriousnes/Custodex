using Custodex.Abstractions;

using Dapper;

using Npgsql;

namespace Custodex.Storage.Postgres.Index;

/// <summary>
/// Computes the set of objects whose reverse-index rows could change as a consequence
/// of a write (the added union removed tuples). Returns a complete superset: recomputing
/// an unchanged object is harmless; missing a changed one is a correctness bug. Climbs
/// inbound structural-arrow edges and group-membership edges transitively from the changed
/// objects, reading on the write transaction so the closure reflects post-write state.
/// Hard-filters on (store, tenant).
/// </summary>
public static class AffectedClosure
{
    private const string InboundSql = """
        WITH RECURSIVE reached (otype, oid) AS (
            SELECT s.otype COLLATE "C", s.oid COLLATE "C" FROM unnest(@types::text[], @ids::text[]) AS s(otype, oid)
          UNION
            SELECT rt.object_type, rt.object_id
            FROM reached r
            JOIN custodex.relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = r.otype AND rt.subject_id = r.oid
        )
        SELECT DISTINCT otype, oid FROM reached
        """;

    /// <summary>
    /// Returns the distinct <see cref="EntityRef"/>s whose reverse-index rows could be
    /// affected by <paramref name="changed"/>. Seeds from each changed tuple's object then
    /// climbs inbound edges transitively using a recursive CTE executed on
    /// <paramref name="tx"/> so it reads post-write state.
    /// </summary>
    public static async Task<IReadOnlyList<EntityRef>> ComputeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TenantContext t,
        IReadOnlyList<RelationTuple> changed, CancellationToken ct = default)
    {
        if (changed.Count == 0) return [];

        var seedTypes = new List<string>();
        var seedIds = new List<string>();
        var seen = new HashSet<EntityRef>();
        foreach (var tuple in changed)
            if (seen.Add(tuple.Object)) { seedTypes.Add(tuple.Object.Type); seedIds.Add(tuple.Object.Id); }

        var rows = await conn.QueryAsync<(string Otype, string Oid)>(new CommandDefinition(InboundSql,
            new { store = t.Store, tenant = t.Tenant, types = seedTypes.ToArray(), ids = seedIds.ToArray() },
            transaction: tx, cancellationToken: ct));

        return rows.Select(r => new EntityRef(r.Otype, r.Oid)).Distinct().ToList();
    }
}
