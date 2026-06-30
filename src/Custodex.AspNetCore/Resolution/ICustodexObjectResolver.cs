using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>
/// Resolves the object a decision is evaluated against. Registered resolvers are tried in order;
/// the first that returns <see langword="true"/> wins. Register a custom resolver to map an
/// application's domain object (passed as an authorization resource) to an <see cref="EntityRef"/>.
/// </summary>
public interface ICustodexObjectResolver
{
    /// <summary>Attempts to resolve the object for the current decision.</summary>
    /// <param name="context">The resolution inputs.</param>
    /// <param name="entity">The resolved object when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an object was resolved; otherwise <see langword="false"/>.</returns>
    bool TryResolve(CustodexResolutionContext context, out EntityRef entity);
}
