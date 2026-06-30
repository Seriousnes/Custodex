using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class PolicyProviderTests
{
    private static CustodexPolicyProvider Provider(CustodexAuthorizationOptions? options = null) =>
        new(Options.Create(new AuthorizationOptions()),
            Options.Create(options ?? new CustodexAuthorizationOptions()));

    [Fact]
    public async Task Custodex_policy_name_yields_authenticated_user_plus_requirement()
    {
        var policy = await Provider().GetPolicyAsync("custodex:thing:view");

        policy.ShouldNotBeNull();
        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
        var requirement = policy.Requirements.OfType<CustodexRequirement>().ShouldHaveSingleItem();
        requirement.ObjectType.ShouldBe("thing");
        requirement.Permission.ShouldBe("view");
    }

    [Fact]
    public async Task Non_custodex_policy_name_is_not_handled()
    {
        var policy = await Provider().GetPolicyAsync("SomeExistingPolicy");

        policy.ShouldBeNull();
    }

    [Fact]
    public async Task Wrong_segment_count_is_not_handled()
    {
        (await Provider().GetPolicyAsync("custodex:thing")).ShouldBeNull();
        (await Provider().GetPolicyAsync("custodex:thing:view:extra")).ShouldBeNull();
        (await Provider().GetPolicyAsync("custodex::view")).ShouldBeNull();
    }

    [Fact]
    public async Task Same_name_returns_the_cached_policy_instance()
    {
        var provider = Provider();

        var first = await provider.GetPolicyAsync("custodex:thing:view");
        var second = await provider.GetPolicyAsync("custodex:thing:view");

        first.ShouldBeSameAs(second);
    }

    [Fact]
    public async Task Custom_prefix_and_separator_are_honoured()
    {
        var provider = Provider(new CustodexAuthorizationOptions { PolicyPrefix = "cdx", PolicySeparator = "/" });

        var policy = await provider.GetPolicyAsync("cdx/thing/view");

        policy.ShouldNotBeNull();
        policy.Requirements.OfType<CustodexRequirement>().ShouldHaveSingleItem().Permission.ShouldBe("view");
    }
}
