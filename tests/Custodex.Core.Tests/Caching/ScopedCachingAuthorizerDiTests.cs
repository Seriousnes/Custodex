using Custodex.Abstractions;
using Custodex.Core.Caching;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Core.Tests.Caching;

public class ScopedCachingAuthorizerDiTests
{
    private static ServiceCollection BuildServices(CountingAuthorizer inner)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizer>(inner);
        services.AddSingleton<ISchemaStore>(new InMemorySchemaStore());
        services.AddSingleton<ICacheStore>(new InMemoryCacheStore());
        services.AddCustodexDecisionCache();
        return services;
    }

    [Fact]
    public void The_decorator_is_registered_scoped_over_the_singleton_inner()
    {
        var services = BuildServices(CountingAuthorizer.Returning(true));

        var decoratorDescriptors = services
            .Where(d => d.ServiceType == typeof(IAuthorizer) && !d.IsKeyedService)
            .ToArray();
        decoratorDescriptors.ShouldHaveSingleItem();
        decoratorDescriptors[0].Lifetime.ShouldBe(ServiceLifetime.Scoped);

        services.ShouldContain(d =>
            d.ServiceType == typeof(IAuthorizer) && d.IsKeyedService && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void IAuthorizer_resolves_to_the_scoped_decorator_with_no_captive_dependency()
    {
        var services = BuildServices(CountingAuthorizer.Returning(true));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAuthorizer>().ShouldBeOfType<ScopedCachingAuthorizer>();
    }

    [Fact]
    public async Task Two_scopes_resolve_distinct_decorators_over_the_same_singleton_inner()
    {
        var inner = CountingAuthorizer.Returning(true);
        var services = BuildServices(inner);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var world = TestWorld.New();
        var subject = world.User(world.SubjectId());
        var request = new CheckRequest(
            world.Tenant,
            new EntityRef(world.EntityType(), world.ObjectId()),
            world.Permission(),
            subject,
            new RequestContext(DateTimeOffset.UnixEpoch, subject, Empty));

        IAuthorizer decoratorA;
        IAuthorizer decoratorB;
        using (var scopeA = provider.CreateScope())
        {
            decoratorA = scopeA.ServiceProvider.GetRequiredService<IAuthorizer>();
            await decoratorA.CheckAsync(request);
        }

        using (var scopeB = provider.CreateScope())
        {
            decoratorB = scopeB.ServiceProvider.GetRequiredService<IAuthorizer>();
            await decoratorB.CheckAsync(request);
        }

        decoratorA.ShouldNotBeSameAs(decoratorB);
        inner.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public void The_invalidation_seam_resolves_to_the_same_scoped_instance_as_the_authorizer()
    {
        var services = BuildServices(CountingAuthorizer.Returning(true));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();
        var invalidation = scope.ServiceProvider.GetRequiredService<ICustodexScopedCache>();

        invalidation.ShouldBeSameAs(authorizer);
    }

    [Fact]
    public void Decoration_is_idempotent_and_configuration_is_applied()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizer>(CountingAuthorizer.Returning(true));
        services.AddCustodexDecisionCache();
        services.AddCustodexDecisionCache(o => o.Enabled = false);

        services.Where(d => d.ServiceType == typeof(IAuthorizer) && !d.IsKeyedService).ShouldHaveSingleItem();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<CustodexCacheOptions>().Enabled.ShouldBeFalse();
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
