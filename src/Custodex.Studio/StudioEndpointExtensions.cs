using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Custodex.Studio;

/// <summary>Maps the Custodex Studio console onto a host's endpoint routing.</summary>
public static class StudioEndpointExtensions
{
    /// <summary>
    /// Maps the Studio root component with the interactive server render mode. The console is reachable
    /// at the route declared by its landing page.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>The same endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapCustodexStudio(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return endpoints;
    }
}
