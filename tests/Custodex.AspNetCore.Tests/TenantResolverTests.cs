using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class TenantResolverTests
{
    private static readonly ClaimsHeaderCustodexTenantResolver Resolver =
        new(Options.Create(new CustodexAuthorizationOptions()));

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public async Task Store_from_claim_tenant_from_header()
    {
        var world = TestWorld.New();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Custodex-Tenant"] = world.Tenant.Tenant;

        var tenant = await Resolver.ResolveAsync(Principal(new Claim("Custodex:store", world.Tenant.Store)), http, CancellationToken.None);

        tenant.ShouldBe(world.Tenant);
    }

    [Fact]
    public async Task Tenant_falls_back_to_claim_when_header_absent()
    {
        var world = TestWorld.New();

        var tenant = await Resolver.ResolveAsync(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:tenant", world.Tenant.Tenant)),
            httpContext: null, CancellationToken.None);

        tenant.ShouldBe(world.Tenant);
    }

    [Fact]
    public async Task Missing_store_does_not_resolve()
    {
        var world = TestWorld.New();

        (await Resolver.ResolveAsync(Principal(new Claim("Custodex:tenant", world.Tenant.Tenant)), null, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Missing_tenant_does_not_resolve()
    {
        var world = TestWorld.New();

        (await Resolver.ResolveAsync(Principal(new Claim("Custodex:store", world.Tenant.Store)), null, CancellationToken.None)).ShouldBeNull();
    }
}
