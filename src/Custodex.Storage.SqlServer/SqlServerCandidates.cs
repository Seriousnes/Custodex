using Dapper;

using Microsoft.Data.SqlClient;

using Custodex.Abstractions;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Produces candidate object ids for ListObjects: a complete superset of the true answer that the
/// confirm-by-Check step then filters. The candidate set is every object of the type that appears in
/// any tuple for the tenant, which is a complete superset because an object the subject can reach must
/// carry at least one inbound tuple. Results are distinct ordinal-sorted strings so pagination cursors
/// are stable across calls.
/// </summary>
public static class SqlServerCandidates
{
    private const string UniverseSql = """
        SELECT DISTINCT object_id
        FROM custodex.relation_tuples
        WHERE store_id = @store AND tenant_id = @tenant AND object_type = @objtype
        ORDER BY object_id
        """;

    /// <summary>
    /// Returns the distinct ordinal-sorted ids of all objects of <paramref name="objectType"/> that
    /// appear as an object in any tuple for the given store and tenant.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TypeUniverseAsync(
        SqlConnection conn, SqlTransaction? tx, TenantContext t, string objectType, CancellationToken ct = default)
    {
        var ids = await conn.QueryAsync<string>(new CommandDefinition(UniverseSql,
            new { store = t.Store, tenant = t.Tenant, objtype = objectType }, transaction: tx, cancellationToken: ct));
        return [.. ids];
    }
}
