using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Custodex.Studio;

/// <summary>Maps the Custodex Studio console onto a host's endpoint routing.</summary>
public static class StudioEndpointExtensions
{
    /// <summary>
    /// Maps the Studio root component with the interactive server render mode. The console is reachable
    /// at the route declared by its landing page and requires an authenticated caller: pass a host
    /// authorization policy name to enforce it, otherwise any authenticated user is admitted. The
    /// console reads and mutates tenant relations, attributes, and schema, so it must never be mapped
    /// anonymously.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="authorizationPolicy">
    /// The name of a host-defined authorization policy to require, or <see langword="null"/> to require
    /// only that the caller is authenticated.
    /// </param>
    /// <returns>The same endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapCustodexStudio(
        this IEndpointRouteBuilder endpoints, string? authorizationPolicy = null)
    {
        var studio = endpoints.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        if (authorizationPolicy is null)
            studio.RequireAuthorization();
        else
            studio.RequireAuthorization(authorizationPolicy);
        return endpoints;
    }
}
