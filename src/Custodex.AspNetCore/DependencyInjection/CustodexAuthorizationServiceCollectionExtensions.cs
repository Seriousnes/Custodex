using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Custodex.AspNetCore;

/// <summary>Registers the Custodex ASP.NET Core authorization adapter.</summary>
public static class CustodexAuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Wires stock ASP.NET Core authorization to the Custodex engine: a dynamic policy provider for
    /// <c>{prefix}:{type}:{permission}</c> policy names, a resource-based handler, and the default
    /// subject, tenant, object, and request-context seams (each replaceable via <c>TryAdd</c>).
    /// Requires an <c>IAuthorizer</c> already registered in DI.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">An optional callback to configure <see cref="CustodexAuthorizationOptions"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddCustodexAuthorization(
        this IServiceCollection services, Action<CustodexAuthorizationOptions>? configure = null)
    {
        services.AddAuthorizationCore();
        services.AddHttpContextAccessor();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuthorizationPolicyProvider, CustodexPolicyProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, CustodexAuthorizationHandler>());

        services.TryAddSingleton<ICustodexSubjectResolver, ClaimsCustodexSubjectResolver>();
        services.TryAddSingleton<ICustodexTenantResolver, ClaimsCustodexTenantResolver>();
        services.TryAddSingleton<IRequestContextFactory, DefaultRequestContextFactory>();

        services.TryAddEnumerable(
        [
            ServiceDescriptor.Singleton<ICustodexObjectResolver, ResourceEntityRefResolver>(),
            ServiceDescriptor.Singleton<ICustodexObjectResolver, ResourceIdResolver>(),
            ServiceDescriptor.Singleton<ICustodexObjectResolver, RouteValueResolver>(),
            ServiceDescriptor.Singleton<ICustodexObjectResolver, RootObjectResolver>(),
        ]);

        services.AddHostedService<CustodexAuthorizerRegistrationCheck>();
        return services;
    }

    /// <summary>
    /// Replaces the tenant resolver with the built-in claim-only resolver, which reads the store and
    /// tenant from claims and ignores the <c>X-Custodex-Tenant</c> header. Use this when the tenant is
    /// fixed by the login token so a client-supplied header cannot request evaluation against another
    /// tenant's grant graph within the same store. Order-independent with respect to
    /// <see cref="AddCustodexAuthorization"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection UseClaimOnlyTenantResolver(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ICustodexTenantResolver, ClaimsCustodexTenantResolver>());
        return services;
    }

    /// <summary>
    /// Replaces the tenant resolver with the built-in header resolver, which honors the
    /// <c>X-Custodex-Tenant</c> header only for a tenant the principal is provably entitled to (a value
    /// present among the principal's tenant claims, compared ordinal). An absent or un-entitled header
    /// falls back to the principal's first tenant claim, so a client-supplied header can never select a
    /// tenant the token does not already grant. Opt in only for callers whose token carries every tenant
    /// they may act as. Order-independent with respect to <see cref="AddCustodexAuthorization"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection UseHeaderTenantResolver(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ICustodexTenantResolver, ClaimsHeaderCustodexTenantResolver>());
        return services;
    }
}
