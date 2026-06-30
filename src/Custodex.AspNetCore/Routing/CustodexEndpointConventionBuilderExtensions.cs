using Microsoft.AspNetCore.Builder;

namespace Custodex.AspNetCore;

/// <summary>Endpoint conventions for Custodex object binding.</summary>
public static class CustodexEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Configures how a Custodex policy on this endpoint binds its object from the route. This only
    /// configures object binding; authorization is still triggered by <c>[Authorize]</c> or
    /// <c>RequireAuthorization</c>.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <param name="routeKey">The route value key holding the object id, or <see langword="null"/> for the conventional keys.</param>
    /// <param name="type">The object type to bind, or <see langword="null"/> to use the policy's type.</param>
    /// <returns>The builder, for chaining.</returns>
    public static TBuilder WithCustodexObject<TBuilder>(this TBuilder builder, string? routeKey = null, string? type = null)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new CustodexObjectBindingMetadata(routeKey, type)));
        return builder;
    }
}
