using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Custodex.Storage.Postgres;
using Dapper;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Seeds a <see cref="GeneratedModel"/> into both an in-memory oracle and a fresh
/// Postgres tenant, then returns the two authorizers so the caller can compare their answers
/// over an identical dataset.</summary>
public static partial class DifferentialHarness
{
    /// <summary>Seeds <paramref name="model"/> into the in-memory oracle stores and into a fresh
    /// <c>(<paramref name="store"/>, "t")</c> tenant in Postgres, then constructs one
    /// <see cref="EngineDrivenAuthorizer"/> and one <see cref="NpgsqlCteAuthorizer"/>, both
    /// using the same <see cref="NullConditionEvaluator"/>.</summary>
    /// <param name="fx">The shared Postgres fixture whose container is already running and migrated.</param>
    /// <param name="model">The schema, tuples, and attributes to seed into both authorizers.</param>
    /// <param name="store">A unique store identifier for this seeding; isolates Postgres state between test runs.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A tuple of the oracle (in-memory) and the CTE (Postgres) authorizers seeded with identical data.</returns>
    public static async Task<(EngineDrivenAuthorizer Oracle, NpgsqlCteAuthorizer Cte)> BuildAsync(
        PostgresFixture fx, GeneratedModel model, string store, CancellationToken ct = default)
    {
        var tenant = new TenantContext(store, "t");
        var conditions = new NullConditionEvaluator();

        var memSchema = new InMemorySchemaStore();
        var memRelations = new InMemoryRelationStore();
        var memAttributes = new InMemoryAttributeStore();
        var memUow = new NoOpUnitOfWork();
        await memSchema.SetActiveAsync(store, model.Schema, memUow, ct);
        if (model.Tuples.Count > 0)
            await memRelations.WriteAsync(tenant, model.Tuples, [], memUow, ct);
        foreach (var (obj, attrs) in model.Attributes)
            await memAttributes.SetAsync(tenant, obj, attrs, memUow, ct);
        await memUow.CommitAsync(ct);
        var oracle = new EngineDrivenAuthorizer(memSchema, memRelations, memAttributes, conditions);

        var pgSchema = new NpgsqlSchemaStore(fx.ConnectionString);
        var pgAttributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        await using (var u = await factory.BeginAsync(ct))
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(
                "INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
                new { s = store }, uow.Transaction);
            await uow.Connection.ExecuteAsync(
                "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
                new { s = store, t = tenant.Tenant }, uow.Transaction);
            await pgSchema.SetActiveAsync(store, model.Schema, u, ct);
            var pgRelations = new NpgsqlRelationStore(fx.ConnectionString);
            if (model.Tuples.Count > 0)
                await pgRelations.WriteAsync(tenant, model.Tuples, [], u, ct);
            foreach (var (obj, attrs) in model.Attributes)
                await pgAttributes.SetAsync(tenant, obj, attrs, u, ct);
            await u.CommitAsync(ct);
        }
        var cte = new NpgsqlCteAuthorizer(fx.ConnectionString, pgSchema, pgAttributes, conditions);

        return (oracle, cte);
    }
}
