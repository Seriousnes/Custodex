using Custodex.Abstractions;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class AddCustodexClientTests
{
    [Fact]
    public void AddCustodexClient_registers_all_five_interfaces()
    {
        var services = new ServiceCollection();
        services.AddCustodexClient("http://localhost:9999");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAuthorizer>().ShouldBeOfType<GrpcAuthorizer>();
        provider.GetRequiredService<IRelationManager>().ShouldBeOfType<GrpcRelationManager>();
        provider.GetRequiredService<ISchemaManager>().ShouldBeOfType<GrpcSchemaManager>();
        provider.GetRequiredService<IStoreManager>().ShouldBeOfType<GrpcStoreManager>();
        provider.GetRequiredService<ITenantManager>().ShouldBeOfType<GrpcTenantManager>();
    }
}
