using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Caching;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests.Di;

public class UseSqliteTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private sealed class StubConditionEvaluator : IConditionEvaluator
    {
        public bool Evaluate(
            ConditionDef definition, ConditionRef invocation,
            IReadOnlyDictionary<string, object?> resourceAttributes, RequestContext context) => true;
    }

    [Fact]
    public void UseSqlite_registers_authorizer_stores_and_managers()
    {
        var services = new ServiceCollection();
        services.AddCustodex()
            .UseSqlite(fx.ConnectionString)
            .UseSchema(new SchemaBuilder("v1")
                .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))));

        var provider = services.BuildServiceProvider();

        provider.GetService<IAuthorizer>().ShouldNotBeNull();
        provider.GetService<IRelationStore>().ShouldNotBeNull();
        provider.GetService<ISchemaStore>().ShouldNotBeNull();
        provider.GetService<IAttributeStore>().ShouldNotBeNull();
        provider.GetService<IChangeLogStore>().ShouldNotBeNull();
        provider.GetService<ICacheStore>().ShouldNotBeNull();
        provider.GetService<IUnitOfWorkFactory>().ShouldNotBeNull();
        provider.GetService<IRelationManager>().ShouldNotBeNull();
        provider.GetService<ISchemaManager>().ShouldNotBeNull();
        provider.GetService<IStoreManager>().ShouldNotBeNull();
        provider.GetService<ITenantManager>().ShouldNotBeNull();
    }

    [Fact]
    public void UseSqlite_authorizer_is_the_scoped_cache_over_the_engine_driven_path()
    {
        var services = new ServiceCollection();
        services.AddCustodex().UseSqlite(fx.ConnectionString);

        services.Where(d => d.ServiceType == typeof(IAuthorizer) && !d.IsKeyedService)
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAuthorizer>().ShouldBeOfType<ScopedCachingAuthorizer>();
        provider.GetRequiredService<EngineDrivenAuthorizer>().ShouldNotBeNull();
    }

    [Fact]
    public void Custom_condition_evaluator_registered_first_wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConditionEvaluator, StubConditionEvaluator>();
        services.AddCustodex().UseSqlite(fx.ConnectionString);
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConditionEvaluator>().ShouldBeOfType<StubConditionEvaluator>();
    }
}
