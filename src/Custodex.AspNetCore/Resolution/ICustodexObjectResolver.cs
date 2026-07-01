using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>
/// Resolves the object a decision is evaluated against. Registered resolvers are tried in order;
/// the first that returns a non-<see langword="null"/> value wins. Register a custom resolver to map an
/// application's domain object (passed as an authorization resource) to an <see cref="EntityRef"/>.
/// </summary>
public interface ICustodexObjectResolver
{
    /// <summary>Attempts to resolve the object for the current decision.</summary>
    /// <param name="context">The resolution inputs.</param>
    /// <param name="cancellationToken">A token that is cancelled when the request is aborted.</param>
    /// <returns>The resolved object, or <see langword="null"/> when this resolver does not resolve one.</returns>
    ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken);
}
