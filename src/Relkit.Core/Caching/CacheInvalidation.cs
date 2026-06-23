using Relkit.Abstractions;

namespace Relkit.Core.Caching;

/// <summary>
/// The cache-invalidation integration point for the write path. The M1 write path
/// calls <see cref="OnWriteAsync"/> inside the same transaction as a tuple/attribute
/// write, so the epoch bump commits atomically with the data change (spec §9.2).
/// </summary>
public static class CacheInvalidation
{
    public static Task OnWriteAsync(ICacheStore cache, TenantContext tenant, IUnitOfWork uow, CancellationToken ct = default)
        => cache.BumpEpochAsync(tenant, uow, ct);
}
