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

    private static readonly ClaimsCustodexTenantResolver ClaimOnlyResolver =
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

    [Fact]
    public async Task ClaimOnly_ignores_the_header_and_uses_the_tenant_claim()
    {
        var world = TestWorld.New();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Custodex-Tenant"] = "spoofed-tenant";

        var tenant = await ClaimOnlyResolver.ResolveAsync(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:tenant", world.Tenant.Tenant)),
            http, CancellationToken.None);

        tenant.ShouldBe(world.Tenant);
    }

    [Fact]
    public async Task ClaimOnly_resolves_store_and_tenant_from_claims_without_http_context()
    {
        var world = TestWorld.New();

        var tenant = await ClaimOnlyResolver.ResolveAsync(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:tenant", world.Tenant.Tenant)),
            httpContext: null, CancellationToken.None);

        tenant.ShouldBe(world.Tenant);
    }

    [Fact]
    public async Task ClaimOnly_missing_store_claim_does_not_resolve()
    {
        var world = TestWorld.New();

        (await ClaimOnlyResolver.ResolveAsync(Principal(new Claim("Custodex:tenant", world.Tenant.Tenant)), null, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ClaimOnly_missing_tenant_claim_does_not_resolve_even_with_header()
    {
        var world = TestWorld.New();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Custodex-Tenant"] = world.Tenant.Tenant;

        (await ClaimOnlyResolver.ResolveAsync(Principal(new Claim("Custodex:store", world.Tenant.Store)), http, CancellationToken.None)).ShouldBeNull();
    }
}
