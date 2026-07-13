using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using MudBlazor.Services;

namespace Custodex.Studio;

/// <summary>Registers the services the Custodex Studio console needs in a host's container.</summary>
public static class StudioServiceCollectionExtensions
{
    /// <summary>
    /// Adds Razor component rendering with the interactive server render mode, flows the caller's
    /// authentication state into the render pipeline so the console can enforce authorization, and
    /// registers the per-session <see cref="StudioConnectionState"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCustodexStudio(this IServiceCollection services)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddMudServices();
        services.TryAddScoped<StudioConnectionState>();
        return services;
    }
}
