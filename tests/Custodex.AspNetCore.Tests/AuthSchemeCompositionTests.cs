using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public sealed class AuthSchemeCompositionTests
{
    [Fact]
    public async Task Decide_policy_authenticates_against_the_custodex_scheme_not_the_host_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(o => o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie();
        services.AddCustodexService(new ConfigurationBuilder().Build());

        var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var decide = await policyProvider.GetPolicyAsync(CustodexServiceCollectionExtensions.DecidePolicy);
        decide.ShouldNotBeNull();
        decide!.AuthenticationSchemes.ShouldContain("Custodex-any");
    }

    [Fact]
    public async Task Manage_policy_authenticates_against_the_custodex_scheme_not_the_host_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(o => o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie();
        services.AddCustodexService(new ConfigurationBuilder().Build());

        var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var manage = await policyProvider.GetPolicyAsync(CustodexServiceCollectionExtensions.ManagePolicy);
        manage.ShouldNotBeNull();
        manage!.AuthenticationSchemes.ShouldContain("Custodex-any");
    }

    [Fact]
    public void AddCustodexService_does_not_override_the_host_default_scheme()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(o => o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie();
        services.AddCustodexService(new ConfigurationBuilder().Build());

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Authentication.AuthenticationOptions>>().Value;
        options.DefaultScheme.ShouldBe(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
