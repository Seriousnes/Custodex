namespace Custodex.AspNetCore;

/// <summary>
/// Endpoint metadata that overrides how the route-value object resolver binds an object: which route
/// key holds the id, and the object type to use. Attach it with
/// <see cref="CustodexEndpointConventionBuilderExtensions.WithCustodexObject{TBuilder}"/>.
/// </summary>
public sealed class CustodexObjectBindingMetadata
{
    /// <summary>Creates the metadata.</summary>
    /// <param name="routeKey">The route value key holding the object id, or <see langword="null"/> to use the conventional keys.</param>
    /// <param name="objectType">The object type to bind, or <see langword="null"/> to use the policy's type.</param>
    public CustodexObjectBindingMetadata(string? routeKey = null, string? objectType = null)
    {
        RouteKey = routeKey;
        ObjectType = objectType;
    }

    /// <summary>The route value key holding the object id, or <see langword="null"/>.</summary>
    public string? RouteKey { get; }

    /// <summary>The object type to bind, or <see langword="null"/>.</summary>
    public string? ObjectType { get; }
}
