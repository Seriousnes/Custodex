using Custodex.Studio.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using MudBlazor.Services;

namespace Custodex.Studio;

/// <summary>Registers the services the Custodex Studio console needs in a host's container.</summary>
public static class StudioServiceCollectionExtensions
{
    /// <summary>
    /// Adds Razor component rendering with the interactive server render mode and registers the
    /// per-session <see cref="StudioConnectionState"/> along with defaults that let the console
    /// compose standalone: an in-memory <see cref="IStudioViewStore"/> so saved views work with no
    /// configuration, and <see cref="TimeProvider.System"/>. Register a durable view store (for
    /// example with <see cref="AddCustodexStudioPostgresViewStore"/>) before or after this call to
    /// override the in-memory default.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCustodexStudio(this IServiceCollection services)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.TryAddScoped<StudioConnectionState>();
        services.TryAddSingleton<IStudioViewStore, InMemoryStudioViewStore>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    /// Registers the durable Postgres-backed <see cref="IStudioViewStore"/> so saved console views
    /// persist across restarts. The store owns its own <c>studio_view</c> table, creating it on
    /// first use; it is independent of the engine's storage migrations.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="connectionString">The Postgres connection string for the Custodex database.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCustodexStudioPostgresViewStore(
        this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IStudioViewStore>(_ => new PostgresStudioViewStore(connectionString));
        return services;
    }
}
