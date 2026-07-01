using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.AspNetCore.Tests.Caching;

public class DecisionCacheHandlerIntegrationTests
{
    private sealed record Fixture(
        ServiceProvider Provider, CountingAuthorizer Authorizer, ClaimsPrincipal User, EntityRef Resource, string Policy);

    private static async Task<Fixture> BuildAsync(CountingAuthorizer authorizer)
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var permission = world.Permission();
        var objId = world.ObjectId();
        var subjectId = world.SubjectId();

        var schemaStore = new InMemorySchemaStore();
        await schemaStore.SetActiveAsync(world.Tenant.Store, new Schema("v1", [], []), new NoOpUnitOfWork());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuthorizer>(authorizer);
        services.AddSingleton<ISchemaStore>(schemaStore);
        services.AddSingleton<ICacheStore>(new InMemoryCacheStore());
        services.AddCustodexAuthorization(o => o.SubjectType = world.UserType);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, subjectId),
            new Claim(CustodexClaimTypes.Store, world.Tenant.Store),
            new Claim(CustodexClaimTypes.Tenant, world.Tenant.Tenant),
        ], "Test"));

        return new Fixture(
            services.BuildServiceProvider(),
            authorizer,
            user,
            new EntityRef(objType, objId),
            $"custodex:{objType}:{permission}");
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
}
