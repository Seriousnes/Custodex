using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Custodex.Studio;

/// <summary>Registers the services the Custodex Studio console needs in a host's container.</summary>
public static class StudioServiceCollectionExtensions
{
    /// <summary>
    /// Adds Razor component rendering with the interactive server render mode and registers the
    /// per-session <see cref="StudioConnectionState"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCustodexStudio(this IServiceCollection services)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.TryAddScoped<StudioConnectionState>();
        return services;
    }
}
