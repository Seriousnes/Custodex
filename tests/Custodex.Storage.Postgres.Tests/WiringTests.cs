using Custodex.Abstractions;
using Custodex.Core;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

public class WiringTests
{
    [Fact]
    public void Storage_assembly_is_referenced()
    {
        typeof(MigrationRunner).Assembly.GetName().Name
            .ShouldBe("Custodex.Storage.Postgres");
    }

    [Fact]
    public void UsePostgres_registers_cache_store_factory_and_cache_store()
    {
        var services = new ServiceCollection();
        services.AddCustodex().UsePostgres("Host=localhost;Database=test;Username=u;Password=p");

        var provider = services.BuildServiceProvider();
        provider.GetService<PostgresCacheStoreFactory>().ShouldNotBeNull();
        provider.GetService<ICacheStore>().ShouldBeOfType<PostgresCacheStore>();
    }
}
