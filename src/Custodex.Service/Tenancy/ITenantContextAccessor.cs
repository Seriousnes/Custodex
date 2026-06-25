using Custodex.Abstractions;

namespace Custodex.Service.Tenancy;

/// <summary>
/// Request-scoped accessor for the resolved <see cref="TenantContext"/>.
/// Populated by <see cref="TenantResolutionMiddleware"/> after authentication succeeds.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>The store and tenant resolved from the authenticated principal and request headers.</summary>
    TenantContext Current { get; }
}
