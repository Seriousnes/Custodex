using Custodex.Abstractions;
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_the_policy_provider_handler_and_default_seams()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuthorizer>(new FakeAuthorizer(new CheckResult(true)));

        services.AddCustodexAuthorization();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAuthorizationPolicyProvider>().ShouldBeOfType<CustodexPolicyProvider>();
        provider.GetServices<IAuthorizationHandler>().OfType<CustodexAuthorizationHandler>().ShouldHaveSingleItem();
        provider.GetRequiredService<ICustodexSubjectResolver>().ShouldBeOfType<ClaimsCustodexSubjectResolver>();
        provider.GetRequiredService<ICustodexTenantResolver>().ShouldBeOfType<ClaimsHeaderCustodexTenantResolver>();
        provider.GetRequiredService<IRequestContextFactory>().ShouldBeOfType<DefaultRequestContextFactory>();
    }

    [Fact]
    public void Object_resolvers_register_in_priority_order()
    {
        var services = new ServiceCollection();
        services.AddCustodexAuthorization();

        var provider = services.BuildServiceProvider();
        var resolvers = provider.GetServices<ICustodexObjectResolver>().ToArray();

        resolvers.Select(r => r.GetType()).ShouldBe(
        [
            typeof(ResourceEntityRefResolver),
            typeof(ResourceIdResolver),
            typeof(RouteValueResolver),
            typeof(RootObjectResolver),
        ]);
    }

    [Fact]
    public void Applies_the_configure_callback()
    {
        var services = new ServiceCollection();

        services.AddCustodexAuthorization(o => o.SubjectType = "principal");

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CustodexAuthorizationOptions>>()
            .Value.SubjectType.ShouldBe("principal");
    }

    [Fact]
    public void A_consumer_resolver_takes_priority_over_the_defaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICustodexSubjectResolver, OverrideSubjectResolver>();

        services.AddCustodexAuthorization();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICustodexSubjectResolver>().ShouldBeOfType<OverrideSubjectResolver>();
    }

    [Fact]
    public void Validation_throws_when_no_authorizer_is_registered()
    {
        var services = new ServiceCollection();
        services.AddCustodexAuthorization();
        var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => CustodexAuthorizationValidation.EnsureAuthorizerRegistered(provider));
    }

    [Fact]
    public void Validation_passes_when_an_authorizer_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizer>(new FakeAuthorizer(new CheckResult(true)));
        services.AddCustodexAuthorization();
        var provider = services.BuildServiceProvider();

        Should.NotThrow(() => CustodexAuthorizationValidation.EnsureAuthorizerRegistered(provider));
    }

    private sealed class OverrideSubjectResolver : ICustodexSubjectResolver
    {
        public bool TryResolve(System.Security.Claims.ClaimsPrincipal user, out SubjectRef subject)
        {
            subject = default;
            return false;
        }
    }
}
