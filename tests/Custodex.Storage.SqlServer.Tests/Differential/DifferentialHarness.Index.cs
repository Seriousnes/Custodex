using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.Storage.SqlServer.Index;

namespace Custodex.Storage.SqlServer.Tests.Differential;

public static partial class DifferentialHarness
{
    /// <summary>Seeds <paramref name="model"/> into both authorizers (via <see cref="BuildAsync"/>),
    /// performs a full reverse-index rebuild for the seeded tenant, then returns the in-memory oracle
    /// and an <see cref="IndexedAuthorizer"/> backed by that rebuilt index. Both authorizers evaluate
    /// conditions with the same real evaluator, so a conditioned grant's index row is re-checked
    /// rather than returned blind.</summary>
    public static async Task<(EngineDrivenAuthorizer Oracle, IndexedAuthorizer Indexed)> BuildIndexedAsync(
        SqlServerFixture fx, GeneratedModel model, string store, CancellationToken ct = default)
    {
        var (oracle, cte) = await BuildAsync(fx, model, store, ct);
        var tenant = new TenantContext(store, "t");
        var schemas = new SqlServerSchemaStore(fx.ConnectionString);
        var relations = new SqlServerRelationStore(fx.ConnectionString);
        var attributes = new SqlServerAttributeStore(fx.ConnectionString);
        var index = new SqlServerIndexStore(fx.ConnectionString);
        var rebuilder = new ReverseIndexRebuilder(fx.ConnectionString, schemas, relations, attributes, index);
        var factory = new SqlServerUnitOfWorkFactory(fx.ConnectionString);
        await using (var u = await factory.BeginAsync(ct))
        {
            await rebuilder.RebuildAsync(tenant, u, ct);
            await u.CommitAsync(ct);
        }
        var indexed = new IndexedAuthorizer(cte, index, schemas);
        return (oracle, indexed);
    }

    /// <summary>Performs a full rebuild of the existing tenant's reverse index in place (clear +
    /// recompute the current tuple state), so the index-backed authorizer reflects a fresh rebuild
    /// of the final state.</summary>
    public static async Task RebuildInPlaceAsync(SqlServerFixture fx, string store, CancellationToken ct = default)
    {
        var tenant = new TenantContext(store, "t");
        var schemas = new SqlServerSchemaStore(fx.ConnectionString);
        var relations = new SqlServerRelationStore(fx.ConnectionString);
        var attributes = new SqlServerAttributeStore(fx.ConnectionString);
        var index = new SqlServerIndexStore(fx.ConnectionString);
        var rebuilder = new ReverseIndexRebuilder(fx.ConnectionString, schemas, relations, attributes, index);
        var factory = new SqlServerUnitOfWorkFactory(fx.ConnectionString);
        await using var u = await factory.BeginAsync(ct);
        await rebuilder.RebuildAsync(tenant, u, ct);
        await u.CommitAsync(ct);
    }
}
