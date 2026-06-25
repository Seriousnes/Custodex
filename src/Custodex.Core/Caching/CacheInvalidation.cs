using Custodex.Abstractions;

namespace Custodex.Core.Caching;

internal static class CacheInvalidation
{
    public static Task OnWriteAsync(ICacheStore cache, TenantContext tenant, IUnitOfWork uow, CancellationToken ct = default)
        => cache.BumpEpochAsync(tenant, uow, ct);
}
