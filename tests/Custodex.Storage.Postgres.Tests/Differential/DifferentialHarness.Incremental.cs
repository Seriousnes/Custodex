using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Index;
using Dapper;

namespace Custodex.Storage.Postgres.Tests.Differential;

public static partial class DifferentialHarness
{
    /// <summary>Seeds the schema into an in-memory oracle over the replayed final tuple set, and into a fresh Postgres tenant whose reverse index is built incrementally by applying <paramref name="ops"/> one transaction at a time, returning the oracle and the index-backed authorizer for comparison.</summary>
    public static async Task<(EngineDrivenAuthorizer Oracle, IndexedAuthorizer Indexed)> BuildIncrementalAsync(
        PostgresFixture fx, Schema schema, IReadOnlyList<WriteOp> ops, string store, CancellationToken ct = default)
    {
        var tenant = new TenantContext(store, "t");
        var conditions = new NullConditionEvaluator();

        var memSchema = new InMemorySchemaStore();
        var memRelations = new InMemoryRelationStore();
        var memAttributes = new InMemoryAttributeStore();
        var memUow = new NoOpUnitOfWork();
        await memSchema.SetActiveAsync(store, schema, memUow, ct);
        var finalTuples = ReplayOps(ops);
        if (finalTuples.Count > 0)
            await memRelations.WriteAsync(tenant, finalTuples, [], memUow, ct);
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
            await pgSchema.SetActiveAsync(store, schema, u, ct);
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

    /// <summary>Performs a full rebuild of the existing tenant's reverse index in place (clear + recompute the current tuple state), so the index-backed authorizer reflects a fresh rebuild of the final state.</summary>
    public static async Task RebuildInPlaceAsync(PostgresFixture fx, string store, CancellationToken ct = default)
    {
        var tenant = new TenantContext(store, "t");
        var pgSchema = new NpgsqlSchemaStore(fx.ConnectionString);
        var pgRelations = new NpgsqlRelationStore(fx.ConnectionString);
        var pgAttributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var index = new NpgsqlIndexStore(fx.ConnectionString);
        var rebuilder = new ReverseIndexRebuilder(fx.ConnectionString, pgSchema, pgRelations, pgAttributes, index);
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        await using var u = await factory.BeginAsync(ct);
        await rebuilder.RebuildAsync(tenant, u, ct);
        await u.CommitAsync(ct);
    }

    private static IReadOnlyList<RelationTuple> ReplayOps(IReadOnlyList<WriteOp> ops)
    {
        var live = new Dictionary<string, RelationTuple>(StringComparer.Ordinal);
        foreach (var op in ops)
        {
            var key = $"{op.Tuple.Object.Type}|{op.Tuple.Object.Id}|{op.Tuple.Relation}|{op.Tuple.Subject.Type}|{op.Tuple.Subject.Id}|{op.Tuple.Subject.Relation ?? ""}";
            if (op.Add) live[key] = op.Tuple;
            else live.Remove(key);
        }
        return live.Values.ToList();
    }
}
