using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.Core.Caching;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.AspNetCore.Tests.Caching;

public class DecisionCacheHandlerIntegrationTests
{
    private sealed record Fixture(
        ServiceProvider Provider,
        CountingAuthorizer Authorizer,
        InMemoryCacheStore CacheStore,
        ClaimsPrincipal User,
        EntityRef Resource,
        string Policy,
        CheckRequest DirectRequest,
        TenantContext Tenant);

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttrs =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static async Task<Fixture> BuildAsync(CountingAuthorizer authorizer)
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var permission = world.Permission();
        var objId = world.ObjectId();
        var subjectId = world.SubjectId();

        var schemaStore = new InMemorySchemaStore();
        await schemaStore.SetActiveAsync(world.Tenant.Store, new Schema("v1", [], []), new NoOpUnitOfWork());
        var cacheStore = new InMemoryCacheStore();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuthorizer>(authorizer);
        services.AddSingleton<ISchemaStore>(schemaStore);
        services.AddSingleton<ICacheStore>(cacheStore);
        services.AddCustodexDecisionCache(o => o.EpochRefreshInterval = TimeSpan.Zero);
        services.AddCustodexAuthorization(o => o.SubjectType = world.UserType);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, subjectId),
            new Claim(CustodexClaimTypes.Store, world.Tenant.Store),
            new Claim(CustodexClaimTypes.Tenant, world.Tenant.Tenant),
        ], "Test"));

        var resource = new EntityRef(objType, objId);
        var subject = new SubjectRef(world.UserType, subjectId);
        var directRequest = new CheckRequest(
            world.Tenant, resource, permission, subject,
            new RequestContext(DateTimeOffset.UnixEpoch, subject, EmptyAttrs));

        return new Fixture(
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }),
            authorizer,
            cacheStore,
            user,
            resource,
            $"custodex:{objType}:{permission}",
            directRequest,
            world.Tenant);
    }

    private async Task BumpEpochAsync(Fixture fx)
    {
        var uow = new NoOpUnitOfWork();
        await fx.CacheStore.BumpEpochAsync(fx.Tenant, uow);
        await uow.CommitAsync();
    }

    [Fact]
    public async Task Two_authorizations_for_the_same_policy_and_resource_in_one_scope_hit_the_engine_once()
    {
        var fx = await BuildAsync(CountingAuthorizer.Returning(true));
        using var scope = fx.Provider.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var first = await authz.AuthorizeAsync(fx.User, fx.Resource, fx.Policy);
        var second = await authz.AuthorizeAsync(fx.User, fx.Resource, fx.Policy);

        first.Succeeded.ShouldBeTrue();
        second.Succeeded.ShouldBeTrue();
        fx.Authorizer.CheckCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Two_separate_scopes_each_hit_the_engine()
    {
        var fx = await BuildAsync(CountingAuthorizer.Returning(true));

        using (var scope = fx.Provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(fx.User, fx.Resource, fx.Policy);

        using (var scope = fx.Provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(fx.User, fx.Resource, fx.Policy);

        fx.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task A_direct_check_and_an_adapter_authorization_in_one_scope_share_a_single_engine_call()
    {
        var fx = await BuildAsync(CountingAuthorizer.Returning(true));
        using var scope = fx.Provider.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        (await authorizer.CheckAsync(fx.DirectRequest)).Allowed.ShouldBeTrue();
        fx.Authorizer.CheckCalls.ShouldBe(1);

        var adapter = await authz.AuthorizeAsync(fx.User, fx.Resource, fx.Policy);

        adapter.Succeeded.ShouldBeTrue();
        fx.Authorizer.CheckCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Direct_path_reflects_an_epoch_bump_and_serves_the_flipped_decision()
    {
        var allow = true;
        var fx = await BuildAsync(new CountingAuthorizer(_ => new CheckResult(allow)));
        using var scope = fx.Provider.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();

        (await authorizer.CheckAsync(fx.DirectRequest)).Allowed.ShouldBeTrue();
        fx.Authorizer.CheckCalls.ShouldBe(1);

        allow = false;
        await BumpEpochAsync(fx);

        (await authorizer.CheckAsync(fx.DirectRequest)).Allowed.ShouldBeFalse();
        fx.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Adapter_path_reflects_an_epoch_bump_and_serves_the_flipped_decision()
    {
        var allow = true;
        var fx = await BuildAsync(new CountingAuthorizer(_ => new CheckResult(allow)));
        using var scope = fx.Provider.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        (await authz.AuthorizeAsync(fx.User, fx.Resource, fx.Policy)).Succeeded.ShouldBeTrue();
        fx.Authorizer.CheckCalls.ShouldBe(1);

        allow = false;
        await BumpEpochAsync(fx);

        (await authz.AuthorizeAsync(fx.User, fx.Resource, fx.Policy)).Succeeded.ShouldBeFalse();
        fx.Authorizer.CheckCalls.ShouldBe(2);
    }
}
