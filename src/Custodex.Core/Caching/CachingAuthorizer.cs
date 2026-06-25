using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Core.Caching;

/// <summary>
/// Caching decorator over <see cref="ICacheableAuthorizer"/>. Caches only
/// unconditioned Check results, keyed by (store, tenant, schema_version, object,
/// permission, subject) and validated against the per-tenant epoch. List/Batch/
/// Explain delegate uncached.
/// </summary>
public sealed class CachingAuthorizer : IAuthorizer
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ICacheableAuthorizer _inner;
    private readonly ISchemaStore _schemaStore;
    private readonly ICacheStore _cache;
    private readonly TimeSpan _ttl;

    internal CachingAuthorizer(ICacheableAuthorizer inner, ISchemaStore schemaStore, ICacheStore cache, TimeSpan? ttl = null)
    {
        _inner = inner;
        _schemaStore = schemaStore;
        _cache = cache;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <inheritdoc/>
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        // Explain bypasses the cache so the trace is always complete and fresh.
        if (request.Explain)
            return await _inner.CheckAsync(request, ct);

        var schema = await _schemaStore.GetActiveAsync(request.Tenant.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{request.Tenant.Store}'>");
        var epoch = await _cache.GetEpochAsync(request.Tenant, ct);
        var key = CheckCacheKey.Build(request.Tenant, schema.Version, request.Object, request.Permission, request.Subject);

        var entry = await _cache.GetAsync(key, ct);
        if (entry is not null && entry.Epoch == epoch)
        {
            CustodexDiagnostics.CacheHits.Add(1);
            return new CheckResult(CacheValueCodec.Decode(entry.Value));
        }

        CustodexDiagnostics.CacheMisses.Add(1);
        var (allowed, conditionTouched) = await _inner.CheckInternalAsync(request, ct);

        // Cache only unconditioned results; stamp with the epoch read above.
        if (!conditionTouched)
            await _cache.SetAsync(key, new CacheEntry(CacheValueCodec.Encode(allowed), epoch), _ttl, ct);

        return new CheckResult(allowed);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => _inner.BatchCheckAsync(request, ct);

    /// <inheritdoc/>
    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => _inner.ListObjectsAsync(request, ct);

    /// <inheritdoc/>
    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => _inner.ListSubjectsAsync(request, ct);
}
