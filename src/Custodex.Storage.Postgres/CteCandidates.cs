using Custodex.Abstractions;

using Dapper;

using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Produces candidate object ids for ListObjects: a complete superset of the true answer.
/// The confirm-by-Check step removes false positives. All results are sorted distinct ordinal
/// strings so pagination cursors are stable across calls.
/// </summary>
public static class CteCandidates
{
    private const string ReachableSql = """
        WITH RECURSIVE principals (ptype, pid, prelation) AS (
            SELECT @stype::text COLLATE "C", @sid::text COLLATE "C", @srel::text COLLATE "C"
          UNION
            SELECT rt.object_type, rt.object_id, rt.relation
            FROM principals p
            JOIN custodex.relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = p.ptype AND rt.subject_id = p.pid
             AND COALESCE(rt.subject_relation, '') = COALESCE(p.prelation, '')
        ),
        reached_objects (otype, oid) AS (
            SELECT rt.object_type, rt.object_id
            FROM principals p
            JOIN custodex.relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = p.ptype AND rt.subject_id = p.pid
             AND COALESCE(rt.subject_relation, '') = COALESCE(p.prelation, '')
          UNION
            SELECT rt.object_type, rt.object_id
            FROM reached_objects ro
            JOIN custodex.relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = ro.otype AND rt.subject_id = ro.oid
             AND rt.subject_relation IS NULL
        )
        SELECT DISTINCT oid
        FROM reached_objects
        WHERE otype = @objtype
        ORDER BY oid
        """;

    private const string UniverseSql = """
        SELECT DISTINCT object_id
        FROM custodex.relation_tuples
        WHERE store_id = @store AND tenant_id = @tenant AND object_type = @objtype
        ORDER BY object_id
        """;

    /// <summary>
    /// Returns the distinct ordinal-sorted ids of objects of <paramref name="objectType"/> that are
    /// reverse-reachable from <paramref name="subject"/>: the subject's own inbound tuples, then
    /// transitively climbing nested subject-set edges (any relation on any type) to collect all
    /// ancestor principals, then collecting every object any ancestor appears on, and finally
    /// following structural edges. This is a superset of the ListObjects answer; each id must be
    /// confirmed by a pointwise Check.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReachableObjectIdsAsync(
        NpgsqlConnection conn, TenantContext t, SubjectRef subject, string objectType, CancellationToken ct = default)
    {
        var ids = await conn.QueryAsync<string>(new CommandDefinition(ReachableSql,
            new { store = t.Store, tenant = t.Tenant, stype = subject.Type, sid = subject.Id, srel = subject.Relation, objtype = objectType },
            cancellationToken: ct));
        return ids.ToList();
    }

    /// <summary>
    /// Returns the distinct ordinal-sorted ids of all objects of <paramref name="objectType"/> that
    /// appear as an object in any tuple for the given store and tenant. This covers wildcard grants
    /// (<c>type:*</c>) that reverse traversal does not reach from a concrete subject.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TypeUniverseAsync(
        NpgsqlConnection conn, TenantContext t, string objectType, CancellationToken ct = default)
    {
        var ids = await conn.QueryAsync<string>(new CommandDefinition(UniverseSql,
            new { store = t.Store, tenant = t.Tenant, objtype = objectType }, cancellationToken: ct));
        return ids.ToList();
    }
}
