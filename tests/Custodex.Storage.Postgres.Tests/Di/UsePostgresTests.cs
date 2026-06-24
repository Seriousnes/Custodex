using Microsoft.Extensions.DependencyInjection;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.Postgres;
using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Di;

[Collection("postgres")]
public class UsePostgresTests(PostgresFixture fx)
{
    [Fact]
    public void UsePostgres_registers_authorizer_and_managers()
    {
        var services = new ServiceCollection();
        services.AddCustodex()
            .UsePostgres(fx.ConnectionString)
            .UseSchema(new SchemaBuilder("v1")
                .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))));

        var provider = services.BuildServiceProvider();
        provider.GetService<IAuthorizer>().ShouldNotBeNull();
        provider.GetService<IRelationManager>().ShouldNotBeNull();
        provider.GetService<ISchemaManager>().ShouldNotBeNull();
        provider.GetService<IStoreManager>().ShouldNotBeNull();
        provider.GetService<ITenantManager>().ShouldNotBeNull();
    }

    [Fact]
    public void UsePostgres_authorizer_is_the_cte_primary_path()
    {
        var services = new ServiceCollection();
        services.AddCustodex().UsePostgres(fx.ConnectionString);
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAuthorizer>().ShouldBeOfType<NpgsqlCteAuthorizer>();
    }
}
