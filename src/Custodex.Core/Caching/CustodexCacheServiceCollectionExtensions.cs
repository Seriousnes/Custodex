using Custodex.Abstractions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Custodex.Core.Caching;

/// <summary>
/// Registers the scoped in-process decision cache as a decorator over the already-registered
/// <see cref="IAuthorizer"/>. The storage providers apply it by default, so every check, direct or
/// through the ASP.NET Core adapter, is cached transparently.
/// </summary>
public static class CustodexCacheServiceCollectionExtensions
{
    private const string InnerKey = "Custodex.ScopedCache.Inner";

    /// <summary>
    /// Decorates the last-registered <see cref="IAuthorizer"/> with a scoped
    /// <see cref="ScopedCachingAuthorizer"/>: the inner engine is preserved as a singleton under a
    /// private key, and <see cref="IAuthorizer"/> plus <see cref="ICustodexScopedCache"/> resolve to one
    /// scoped decorator instance per scope. The preserved inner engine is wrapped in a check-latency
    /// metering decorator, so every check that reaches the engine records the
    /// <c>Custodex.check.duration</c> histogram while a decision served from the scoped cache does not.
    /// Idempotent: a second call only applies
    /// <paramref name="configure"/> to the shared <see cref="CustodexCacheOptions"/>.
    /// </summary>
    /// <param name="services">The service collection, which must already have an <see cref="IAuthorizer"/> registered.</param>
    /// <param name="configure">An optional callback to tune the shared <see cref="CustodexCacheOptions"/>.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No <see cref="IAuthorizer"/> is registered yet.</exception>
    public static IServiceCollection AddCustodexDecisionCache(
        this IServiceCollection services, Action<CustodexCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = GetOrAddOptions(services);
        configure?.Invoke(options);
        services.TryAddSingleton(TimeProvider.System);

        if (services.Any(static d => d.ServiceType == typeof(DecisionCacheMarker)))
            return services;

        var inner = services.LastOrDefault(static d => d.ServiceType == typeof(IAuthorizer))
            ?? throw new InvalidOperationException(
                "AddCustodexDecisionCache requires an IAuthorizer already registered. Register the engine with " +
                "AddCustodex().Use<provider>() before enabling the decision cache.");

        services.Remove(inner);
        services.Add(InnerAsKeyedSingleton(inner));

        services.AddScoped(static sp => new ScopedCachingAuthorizer(
            sp.GetRequiredKeyedService<IAuthorizer>(InnerKey),
            sp.GetService<ISchemaStore>(),
            sp.GetService<ICacheStore>(),
            sp.GetRequiredService<CustodexCacheOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IAuthorizer>(static sp => sp.GetRequiredService<ScopedCachingAuthorizer>());
        services.AddScoped<ICustodexScopedCache>(static sp => sp.GetRequiredService<ScopedCachingAuthorizer>());
        services.AddSingleton(new DecisionCacheMarker());

        return services;
    }

    /// <summary>
    /// Tunes the shared <see cref="CustodexCacheOptions"/> the scoped decision cache reads. Order
    /// independent with respect to the storage provider registration, so it may be called before or
    /// after <c>UsePostgres</c> and friends.
    /// </summary>
    /// <param name="builder">The Custodex builder returned by <c>AddCustodex()</c>.</param>
    /// <param name="configure">A callback to tune the shared <see cref="CustodexCacheOptions"/>.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    public static CustodexBuilder ConfigureDecisionCache(
        this CustodexBuilder builder, Action<CustodexCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(GetOrAddOptions(builder.Services));
        return builder;
    }

    private static CustodexCacheOptions GetOrAddOptions(IServiceCollection services)
    {
        if (services.FirstOrDefault(static d => d.ServiceType == typeof(CustodexCacheOptions))?.ImplementationInstance
            is CustodexCacheOptions existing)
            return existing;

        var options = new CustodexCacheOptions();
        services.AddSingleton(options);
        return options;
    }

    private static ServiceDescriptor InnerAsKeyedSingleton(ServiceDescriptor inner)
    {
        if (inner.ImplementationInstance is not null)
            return new ServiceDescriptor(
                typeof(IAuthorizer), InnerKey, new MeteredAuthorizer((IAuthorizer)inner.ImplementationInstance));

        if (inner.ImplementationFactory is not null)
            return new ServiceDescriptor(
                typeof(IAuthorizer), InnerKey,
                (sp, _) => new MeteredAuthorizer((IAuthorizer)inner.ImplementationFactory(sp)), ServiceLifetime.Singleton);

        return new ServiceDescriptor(
            typeof(IAuthorizer), InnerKey,
            (sp, _) => new MeteredAuthorizer(
                (IAuthorizer)ActivatorUtilities.CreateInstance(sp, inner.ImplementationType!)),
            ServiceLifetime.Singleton);
    }

    private sealed class DecisionCacheMarker;
}
