using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Caching;
using Custodex.Core.Conditions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

namespace Custodex.Storage.MySql.Tests.Di;

[Collection("mysql")]
public class UseMySqlTests(MySqlFixture fx)
{
    private sealed class AllowAllConditionEvaluator : IConditionEvaluator
    {
        public bool Evaluate(
            ConditionDef definition, ConditionRef invocation,
            IReadOnlyDictionary<string, object?> resourceAttributes, RequestContext context) => true;
    }

    [Fact]
    public void UseMySql_registers_authorizer_stores_and_managers()
    {
        var services = new ServiceCollection();
        services.AddCustodex()
            .UseMySql(fx.ConnectionString)
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
    public void UseMySql_authorizer_is_the_scoped_cache_over_the_engine_driven_path()
    {
        var services = new ServiceCollection();
        services.AddCustodex().UseMySql(fx.ConnectionString);

        services.Where(d => d.ServiceType == typeof(IAuthorizer) && !d.IsKeyedService)
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);

        var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAuthorizer>().ShouldBeOfType<ScopedCachingAuthorizer>();
    }

    [Fact]
    public void A_condition_evaluator_registered_before_UseMySql_wins()
    {
        var services = new ServiceCollection();
        services.TryAddSingleton<IConditionEvaluator, AllowAllConditionEvaluator>();
        services.AddCustodex().UseMySql(fx.ConnectionString);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConditionEvaluator>().ShouldBeOfType<AllowAllConditionEvaluator>();
    }
}
