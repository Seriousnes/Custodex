using Custodex.Abstractions;

namespace Custodex.AspNetCore;

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

    /// <summary>
    /// Whether the authenticated principal holds the opt-in operator capability. An operator is not
    /// bound to <see cref="AuthenticatedStore"/>: it may act on any store and any tenant, chosen per
    /// call from the request headers. This grants cross-store and cross-tenant reach and is intended
    /// for gated, network-restricted development and administrative use only.
    /// </summary>
    bool IsOperator { get; }

    /// <summary>
    /// Whether the caller may act on <paramref name="store"/>. Returns <see langword="true"/> for an
    /// operator principal (any store) and otherwise only when <paramref name="store"/> matches
    /// <see cref="AuthenticatedStore"/> ordinally. Control-plane operations gate on this before
    /// touching a store.
    /// </summary>
    /// <param name="store">The store the operation targets.</param>
    bool IsStoreAuthorized(string store) =>
        IsOperator || string.Equals(store, AuthenticatedStore, StringComparison.Ordinal);
}
