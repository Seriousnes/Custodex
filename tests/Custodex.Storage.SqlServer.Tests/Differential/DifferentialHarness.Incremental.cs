using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Custodex.Storage.SqlServer.Index;

using Dapper;

namespace Custodex.Storage.SqlServer.Tests.Differential;

public static partial class DifferentialHarness
{
    /// <summary>Seeds the schema and attributes into an in-memory oracle over the replayed final tuple
    /// set, and into a fresh SQL Server tenant whose reverse index is built incrementally by applying
    /// <paramref name="ops"/> one transaction at a time through the maintainer, returning the oracle and
    /// the index-backed authorizer for comparison. Both sides evaluate conditions with the same real
    /// evaluator, so conditioned grants are exercised under genuine condition evaluation.</summary>
    public static async Task<(EngineDrivenAuthorizer Oracle, IndexedAuthorizer Indexed)> BuildIncrementalIndexedAsync(
        SqlServerFixture fx, GeneratedModel model, IReadOnlyList<WriteOp> ops, string store, CancellationToken ct = default)
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

        var sqlSchema = new SqlServerSchemaStore(fx.ConnectionString);
        var sqlRelations = new SqlServerRelationStore(fx.ConnectionString);
        var sqlAttributes = new SqlServerAttributeStore(fx.ConnectionString);
        var index = new SqlServerIndexStore(fx.ConnectionString);
        var factory = new SqlServerUnitOfWorkFactory(fx.ConnectionString);

        await using (var u = await factory.BeginAsync(ct))
        {
            var uow = SqlServerUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(new CommandDefinition(
                "IF NOT EXISTS (SELECT 1 FROM custodex.stores WHERE id = @s) INSERT INTO custodex.stores (id) VALUES (@s);",
                new { s = store }, uow.Transaction, cancellationToken: ct));
            await uow.Connection.ExecuteAsync(new CommandDefinition(
                "IF NOT EXISTS (SELECT 1 FROM custodex.tenants WHERE store_id = @s AND tenant_id = @t) " +
                "INSERT INTO custodex.tenants (store_id, tenant_id) VALUES (@s, @t);",
                new { s = store, t = tenant.Tenant }, uow.Transaction, cancellationToken: ct));
            await sqlSchema.SetActiveAsync(store, model.Schema, u, ct);
            foreach (var (obj, attrs) in model.Attributes)
                await sqlAttributes.SetAsync(tenant, obj, attrs, u, ct);
            await u.CommitAsync(ct);
        }

        var rebuilder = new ReverseIndexRebuilder(fx.ConnectionString, sqlSchema, sqlRelations, sqlAttributes, index);
        await using (var u = await factory.BeginAsync(ct))
        {
            await rebuilder.RebuildAsync(tenant, u, ct);
            await u.CommitAsync(ct);
        }

        var maintainer = new ReverseIndexMaintainer(sqlSchema, sqlRelations, sqlAttributes, index);
        foreach (var op in ops)
        {
            RelationTuple[] add = op.Add ? [op.Tuple] : [];
            RelationTuple[] remove = op.Add ? [] : [op.Tuple];
            await using var u = await factory.BeginAsync(ct);
            await sqlRelations.WriteAsync(tenant, add, remove, u, ct);
            var changed = new List<RelationTuple>(add);
            changed.AddRange(remove);
            await maintainer.MaintainAsync(tenant, changed, u, ct);
            await u.CommitAsync(ct);
        }

        var cte = new SqlServerCteAuthorizer(fx.ConnectionString, sqlSchema, sqlAttributes, conditions);
        var indexed = new IndexedAuthorizer(cte, index, sqlSchema);
        return (oracle, indexed);
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
        return [.. live.Values];
    }
}
