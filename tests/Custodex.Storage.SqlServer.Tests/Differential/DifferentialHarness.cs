using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;

using Dapper;

namespace Custodex.Storage.SqlServer.Tests.Differential;

/// <summary>Seeds a <see cref="GeneratedModel"/> into both an in-memory oracle and a fresh
/// SQL Server tenant, then returns the two authorizers so the caller can compare their answers
/// over an identical dataset.</summary>
public static partial class DifferentialHarness
{
    /// <summary>Seeds <paramref name="model"/> into the in-memory oracle stores and into a fresh
    /// <c>(<paramref name="store"/>, "t")</c> tenant in SQL Server, then constructs one
    /// <see cref="EngineDrivenAuthorizer"/> and one <see cref="SqlServerCteAuthorizer"/>, both
    /// using the same <see cref="CelConditionEvaluator"/> so conditioned tuples are evaluated
    /// identically on both paths.</summary>
    /// <param name="fx">The shared fixture whose container is already running and migrated.</param>
    /// <param name="model">The schema, tuples, and attributes to seed into both authorizers.</param>
    /// <param name="store">A unique store identifier for this seeding; isolates state between test runs.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A tuple of the oracle (in-memory) and the CTE (SQL Server) authorizers seeded with identical data.</returns>
    public static async Task<(EngineDrivenAuthorizer Oracle, SqlServerCteAuthorizer Cte)> BuildAsync(
        SqlServerFixture fx, GeneratedModel model, string store, CancellationToken ct = default)
    {
        var tenant = new TenantContext(store, "t");
        var conditions = new CelConditionEvaluator();

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

        var sqlSchema = new SqlServerSchemaStore(fx.ConnectionString);
        var sqlAttributes = new SqlServerAttributeStore(fx.ConnectionString);
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
            var sqlRelations = new SqlServerRelationStore(fx.ConnectionString);
            if (model.Tuples.Count > 0)
                await sqlRelations.WriteAsync(tenant, model.Tuples, [], u, ct);
            foreach (var (obj, attrs) in model.Attributes)
                await sqlAttributes.SetAsync(tenant, obj, attrs, u, ct);
            await u.CommitAsync(ct);
        }
        var cte = new SqlServerCteAuthorizer(fx.ConnectionString, sqlSchema, sqlAttributes, conditions);

        return (oracle, cte);
    }
}
