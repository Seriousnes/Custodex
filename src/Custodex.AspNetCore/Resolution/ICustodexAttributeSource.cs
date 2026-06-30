namespace Custodex.AspNetCore;

/// <summary>
/// Contributes request-scoped attributes for condition (ABAC) evaluation. Implement and register one
/// to project claims, route values, or headers into the attribute set a decision sees.
/// </summary>
public interface ICustodexAttributeSource
{
    /// <summary>Adds this source's attributes to <paramref name="attributes"/>.</summary>
    /// <param name="attributes">The accumulating attribute set; later sources overwrite earlier keys.</param>
    /// <param name="context">The resolution inputs.</param>
    void Contribute(IDictionary<string, object?> attributes, CustodexResolutionContext context);
}
