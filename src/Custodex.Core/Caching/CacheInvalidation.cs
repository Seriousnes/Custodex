using Custodex.Abstractions;

namespace Custodex.Core.Caching;

/// <summary>
/// The cache-invalidation hook for the write path. Call <see cref="OnWriteAsync"/> within the
/// same unit of work as a tuple or attribute write so the tenant's cache epoch bump commits
/// atomically with the data change, marking every decision cached before the write as stale.
/// </summary>
public static class CacheInvalidation
{
    /// <summary>Invalidates the tenant's cached decisions as part of a data write.</summary>
    /// <param name="cache">The cache store whose epoch is bumped.</param>
    /// <param name="tenant">The store and tenant whose cached decisions are invalidated.</param>
    /// <param name="uow">The unit of work the bump enlists in, committing atomically with the write.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the epoch bump is enlisted.</returns>
    public static Task OnWriteAsync(ICacheStore cache, TenantContext tenant, IUnitOfWork uow, CancellationToken ct = default)
        => cache.BumpEpochAsync(tenant, uow, ct);
}
