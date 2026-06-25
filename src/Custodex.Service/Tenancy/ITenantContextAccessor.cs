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

    /// <summary>
    /// The store carried by the authenticated principal's store claim, independent of any tenant
    /// header. <see langword="null"/> when no store claim is present. Control-plane operations use
    /// this to confirm the targeted store matches the caller's grant.
    /// </summary>
    string? AuthenticatedStore { get; }
}
