using Dapper;
using Npgsql;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres.Index;

/// <summary>
/// Projects the rebuild domain from a tenant's relation tuples: the concrete user subjects
/// and every object grouped by type. The rebuilder crosses these with the schema's
/// (type, permission) declarations to form the full set of grants to probe.
/// </summary>
public static class RebuildEnumeration
{
    /// <summary>
    /// The inputs to a reverse-index rebuild: the candidate query subjects and the candidate
    /// objects partitioned by entity type.
    /// </summary>
    public sealed record RebuildInputs(
        IReadOnlyList<string> Users,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ObjectIdsByType);

    /// <summary>
    /// Reads the tenant's relation tuples once and returns the distinct concrete user subject
    /// ids and all object ids grouped by object type. The wildcard subject <c>user:*</c> is
    /// excluded from <see cref="RebuildInputs.Users"/>; the rebuilder adds it synthetically.
    /// </summary>
    public static async Task<RebuildInputs> LoadAsync(NpgsqlConnection conn, TenantContext t, CancellationToken ct = default)
    {
        var users = (await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT subject_id FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant AND subject_type = 'user' AND subject_id <> '*'
            """, new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct))).ToList();

        var objects = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT DISTINCT object_type AS ObjectType, object_id AS ObjectId FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
            """, new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct));

        var byType = objects
            .GroupBy(o => o.ObjectType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(o => o.ObjectId).ToList(), StringComparer.Ordinal);

        return new RebuildInputs(users, byType);
    }

    private sealed record Row(string ObjectType, string ObjectId);
}
