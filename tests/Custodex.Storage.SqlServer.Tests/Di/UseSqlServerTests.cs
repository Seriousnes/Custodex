using Custodex.Abstractions;
using Custodex.Core;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Di;

[Collection("sqlserver")]
public class UseSqlServerTests(SqlServerFixture fx)
{
    [Fact]
    public void UseSqlServer_registers_authorizer_and_managers()
    {
        var services = new ServiceCollection();
        services.AddCustodex()
            .UseSqlServer(fx.ConnectionString)
            .UseSchema(new SchemaBuilder("v1")
                .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))));

        var provider = services.BuildServiceProvider();
        provider.GetService<IAuthorizer>().ShouldNotBeNull();
        provider.GetService<IRelationManager>().ShouldNotBeNull();
        provider.GetService<ISchemaManager>().ShouldNotBeNull();
        provider.GetService<IStoreManager>().ShouldNotBeNull();
        provider.GetService<ITenantManager>().ShouldNotBeNull();
        provider.GetService<IUnitOfWorkFactory>().ShouldNotBeNull();
        provider.GetService<ICacheStore>().ShouldNotBeNull();
    }

    [Fact]
    public void UseSqlServer_authorizer_is_the_cte_primary_path()
    {
        var services = new ServiceCollection();
        services.AddCustodex().UseSqlServer(fx.ConnectionString);
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAuthorizer>().ShouldBeOfType<SqlServerCteAuthorizer>();
    }
}
