using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.Storage.Postgres.Index;

namespace Custodex.Storage.Postgres.Tests.Differential;

public static partial class DifferentialHarness
{
    /// <summary>Seeds <paramref name="model"/> into both authorizers (via <see cref="BuildAsync"/>),
    /// performs a full reverse-index rebuild for the seeded tenant, then returns the in-memory oracle
    /// and an <see cref="IndexedAuthorizer"/> backed by that rebuilt index.</summary>
    /// <param name="fx">The shared Postgres fixture whose container is already running and migrated.</param>
    /// <param name="model">The schema, tuples, and attributes to seed into both authorizers.</param>
    /// <param name="store">A unique store identifier for this seeding; isolates Postgres state between test runs.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A tuple of the oracle (in-memory) and the index-backed authorizer seeded with identical data.</returns>
    public static async Task<(EngineDrivenAuthorizer Oracle, IndexedAuthorizer Indexed)> BuildIndexedAsync(
        PostgresFixture fx, GeneratedModel model, string store, CancellationToken ct = default)
    {
        var (oracle, cte) = await BuildAsync(fx, model, store, ct);
        var tenant = new TenantContext(store, "t");
        var schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        var relations = new NpgsqlRelationStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var index = new NpgsqlIndexStore(fx.ConnectionString);
        var rebuilder = new ReverseIndexRebuilder(fx.ConnectionString, schemas, relations, attributes, index);
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        await using (var u = await factory.BeginAsync(ct))
        {
            await rebuilder.RebuildAsync(tenant, u, ct);
            await u.CommitAsync(ct);
        }
        var indexed = new IndexedAuthorizer(cte, index, schemas);
        return (oracle, indexed);
    }
}
