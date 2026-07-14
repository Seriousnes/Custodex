using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class CustodexServiceCollectionExtensionsTests
{
    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public void Registers_the_request_scoped_tenant_accessor()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCustodexService(EmptyConfiguration());

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .ShouldBeAssignableTo<ITenantContextAccessor>();
    }

    [Fact]
    public async Task Registers_the_decide_and_manage_policies()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCustodexService(EmptyConfiguration());

        var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        (await policies.GetPolicyAsync(CustodexServiceCollectionExtensions.DecidePolicy)).ShouldNotBeNull();
        (await policies.GetPolicyAsync(CustodexServiceCollectionExtensions.ManagePolicy)).ShouldNotBeNull();
    }

    [Fact]
    public void Binds_api_keys_from_the_configured_section()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Custodex:ApiKeys:0:Key"] = "the-key",
                ["Custodex:ApiKeys:0:Store"] = "the-store",
                ["Custodex:ApiKeys:0:Role"] = "admin",
            })
            .Build();

        services.AddCustodexService(configuration);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<ApiKeyOptions>>().Get("ApiKey");

        var entry = options.Keys.ShouldHaveSingleItem();
        entry.Key.ShouldBe("the-key");
        entry.Store.ShouldBe("the-store");
        entry.Role.ShouldBe("admin");
    }

    [Fact]
    public void Honours_a_custom_configuration_section_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authz:ApiKeys:0:Key"] = "scoped-key",
                ["Authz:ApiKeys:0:Store"] = "scoped-store",
                ["Authz:ApiKeys:0:Role"] = "reader",
            })
            .Build();

        services.AddCustodexService(configuration, "Authz");

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<ApiKeyOptions>>().Get("ApiKey");

        options.Keys.ShouldHaveSingleItem().Key.ShouldBe("scoped-key");
    }
}
