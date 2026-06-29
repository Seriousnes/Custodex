using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Custodex.Storage.Postgres.Index;

using Dapper;

namespace Custodex.Storage.Postgres.Tests.Differential;

public static partial class DifferentialHarness
{
    /// <summary>Seeds the schema and attributes into an in-memory oracle over the replayed final tuple
    /// set, and into a fresh Postgres tenant whose reverse index is built incrementally by applying
    /// <paramref name="ops"/> one transaction at a time through the maintainer, returning the oracle and
    /// the index-backed authorizer for comparison. Both sides evaluate conditions with the same real
    /// evaluator, so conditioned grants are exercised under genuine condition evaluation.</summary>
    public static async Task<(EngineDrivenAuthorizer Oracle, IndexedAuthorizer Indexed)> BuildConditionedIncrementalIndexedAsync(
        PostgresFixture fx, GeneratedModel model, IReadOnlyList<WriteOp> ops, string store, CancellationToken ct = default)
    {
        var tenant = new TenantContext(store, "t");
        var conditions = new CelConditionEvaluator();

        var memSchema = new InMemorySchemaStore();
        var memRelations = new InMemoryRelationStore();
        var memAttributes = new InMemoryAttributeStore();
        var memUow = new NoOpUnitOfWork();
        await memSchema.SetActiveAsync(store, model.Schema, memUow, ct);
        var finalTuples = ReplayOps(ops);
        if (finalTuples.Count > 0)
            await memRelations.WriteAsync(tenant, finalTuples, [], memUow, ct);
        foreach (var (obj, attrs) in model.Attributes)
            await memAttributes.SetAsync(tenant, obj, attrs, memUow, ct);
        await memUow.CommitAsync(ct);
        var oracle = new EngineDrivenAuthorizer(memSchema, memRelations, memAttributes, conditions);

        var pgSchema = new NpgsqlSchemaStore(fx.ConnectionString);
        var pgRelations = new NpgsqlRelationStore(fx.ConnectionString);
        var pgAttributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var index = new NpgsqlIndexStore(fx.ConnectionString);
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using (var u = await factory.BeginAsync(ct))
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(
                "INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = store }, uow.Transaction);
            await uow.Connection.ExecuteAsync(
                "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
                new { s = store, t = tenant.Tenant }, uow.Transaction);
            await pgSchema.SetActiveAsync(store, model.Schema, u, ct);
            foreach (var (obj, attrs) in model.Attributes)
                await pgAttributes.SetAsync(tenant, obj, attrs, u, ct);
            await u.CommitAsync(ct);
        }

        var rebuilder = new ReverseIndexRebuilder(fx.ConnectionString, pgSchema, pgRelations, pgAttributes, index);
        await using (var u = await factory.BeginAsync(ct))
        {
            await rebuilder.RebuildAsync(tenant, u, ct);
            await u.CommitAsync(ct);
        }

        var maintainer = new ReverseIndexMaintainer(pgSchema, pgRelations, pgAttributes, index);
        foreach (var op in ops)
        {
            RelationTuple[] add = op.Add ? [op.Tuple] : [];
            RelationTuple[] remove = op.Add ? [] : [op.Tuple];
            await using var u = await factory.BeginAsync(ct);
            await pgRelations.WriteAsync(tenant, add, remove, u, ct);
            var changed = new List<RelationTuple>(add);
            changed.AddRange(remove);
            await maintainer.MaintainAsync(tenant, changed, u, ct);
            await u.CommitAsync(ct);
        }

        var cte = new NpgsqlCteAuthorizer(fx.ConnectionString, pgSchema, pgAttributes, conditions);
        var indexed = new IndexedAuthorizer(cte, index, pgSchema);
        return (oracle, indexed);
    }
}
