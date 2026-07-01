using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class AuthorizationHandlerTests
{
    private sealed record Harness(
        CustodexAuthorizationHandler Handler,
        ClaimsPrincipal User,
        CustodexRequirement Requirement,
        FakeAuthorizer Authorizer);

    private static Harness Build(
        IAuthorizer authorizer,
        TestWorld world,
        HttpContext? http = null,
        CustodexAuthorizationOptions? options = null,
        ICustodexObjectResolver[]? objectResolvers = null)
    {
        var opts = Options.Create(options ?? new CustodexAuthorizationOptions { SubjectType = world.UserType });
        var fake = authorizer as FakeAuthorizer;

        var subjectResolver = new ClaimsCustodexSubjectResolver(opts);
        var tenantResolver = new ClaimsHeaderCustodexTenantResolver(opts);
        objectResolvers ??=
        [
            new ResourceEntityRefResolver(),
            new ResourceIdResolver(),
            new RouteValueResolver(),
            new RootObjectResolver(opts),
        ];
        var factory = new DefaultRequestContextFactory(new FakeTimeProvider(DateTimeOffset.UnixEpoch), []);
        var accessor = new HttpContextAccessor { HttpContext = http };

        var handler = new CustodexAuthorizationHandler(
            authorizer, subjectResolver, tenantResolver, objectResolvers, factory, accessor, opts,
            NullLogger<CustodexAuthorizationHandler>.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, world.SubjectId()),
            new Claim(CustodexClaimTypes.Store, world.Tenant.Store),
            new Claim(CustodexClaimTypes.Tenant, world.Tenant.Tenant),
        ], "Test"));

        var objType = world.EntityType();
        var requirement = new CustodexRequirement(objType, world.Permission());
        return new Harness(handler, user, requirement, fake!);
    }

    private static AuthorizationHandlerContext ContextFor(Harness h, object? resource = null) =>
        new([h.Requirement], h.User, resource);

    [Fact]
    public async Task Allows_when_engine_allows_and_object_comes_from_resource_entity()
    {
        var world = TestWorld.New();
        var fake = new FakeAuthorizer(new CheckResult(true));
        var h = Build(fake, world);
        var entity = new EntityRef(h.Requirement.ObjectType, world.ObjectId());

        var ctx = ContextFor(h, entity);
        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeTrue();
        h.Authorizer.LastRequest!.Object.ShouldBe(entity);
        h.Authorizer.LastRequest.Permission.ShouldBe(h.Requirement.Permission);
        h.Authorizer.LastRequest.Tenant.ShouldBe(world.Tenant);
        h.Authorizer.LastRequest.Subject.Type.ShouldBe(world.UserType);
    }

    [Fact]
    public async Task Denies_when_engine_denies()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(false)), world);
        var ctx = ContextFor(h, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Denies_when_no_subject_claim()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(true)), world);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var ctx = new AuthorizationHandlerContext([h.Requirement], anonymous, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
        h.Authorizer.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Denies_when_no_object_can_be_resolved()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(true)), world);
        var ctx = ContextFor(h, resource: null);

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
        h.Authorizer.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Denies_when_no_tenant_claims()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(true)), world);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, world.SubjectId())], "Test"));
        var ctx = new AuthorizationHandlerContext([h.Requirement], principal, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
        h.Authorizer.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Engine_exception_denies_by_default()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new InvalidOperationException("boom")), world);
        var ctx = ContextFor(h, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Engine_exception_rethrows_when_configured()
    {
        var world = TestWorld.New();
        var h = Build(
            new FakeAuthorizer(new InvalidOperationException("boom")),
            world,
            options: new CustodexAuthorizationOptions { SubjectType = world.UserType, ThrowOnEvaluationError = true });
        var ctx = ContextFor(h, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await Should.ThrowAsync<InvalidOperationException>(() => h.Handler.HandleAsync(ctx));
    }

    [Fact]
    public async Task Awaits_an_async_object_resolver()
    {
        var world = TestWorld.New();
        var fake = new FakeAuthorizer(new CheckResult(true));
        var entity = new EntityRef(world.EntityType(), world.ObjectId());
        var h = Build(fake, world, objectResolvers: [new AsyncObjectResolver(entity)]);

        var ctx = ContextFor(h, resource: null);
        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeTrue();
        h.Authorizer.LastRequest!.Object.ShouldBe(entity);
    }

    [Fact]
    public async Task Throwing_object_resolver_denies_by_default()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(true)), world, objectResolvers: [new ThrowingObjectResolver()]);
        var ctx = ContextFor(h, resource: null);

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Throwing_object_resolver_rethrows_when_configured()
    {
        var world = TestWorld.New();
        var h = Build(
            new FakeAuthorizer(new CheckResult(true)),
            world,
            options: new CustodexAuthorizationOptions { SubjectType = world.UserType, ThrowOnEvaluationError = true },
            objectResolvers: [new ThrowingObjectResolver()]);
        var ctx = ContextFor(h, resource: null);

        await Should.ThrowAsync<InvalidOperationException>(() => h.Handler.HandleAsync(ctx));
    }

    [Fact]
    public async Task Cancellation_token_flows_to_resolvers()
    {
        var world = TestWorld.New();
        using var cts = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = cts.Token };
        var entity = new EntityRef(world.EntityType(), world.ObjectId());
        var capturing = new CapturingObjectResolver(entity);
        var h = Build(new FakeAuthorizer(new CheckResult(true)), world, http: http, objectResolvers: [capturing]);

        var ctx = ContextFor(h, resource: null);
        await h.Handler.HandleAsync(ctx);

        capturing.Captured.ShouldBe(cts.Token);
    }

    private sealed class AsyncObjectResolver(EntityRef entity) : ICustodexObjectResolver
    {
        public async ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return entity;
        }
    }

    private sealed class ThrowingObjectResolver : ICustodexObjectResolver
    {
        public ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("resolver boom");
    }

    private sealed class CapturingObjectResolver(EntityRef entity) : ICustodexObjectResolver
    {
        public CancellationToken Captured { get; private set; }

        public ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken)
        {
            Captured = cancellationToken;
            return ValueTask.FromResult<EntityRef?>(entity);
        }
    }
}
