using System.Collections.Concurrent;

using Custodex.Abstractions;

namespace Custodex.Core.Caching;

/// <summary>
/// A DI-scoped <see cref="IAuthorizer"/> decorator that memoizes <see cref="CheckAsync"/> decisions for
/// the lifetime of one scope (a Blazor circuit or an HTTP request), so every check, whether issued
/// directly through <see cref="IAuthorizer"/> or through the ASP.NET Core authorization adapter, is
/// reused across the burst a single render pass or request fires without ever leaking a decision across
/// scopes. Soundness comes from stamping each entry with the <c>(schemaVersion, epoch)</c> it was
/// computed under and validating that stamp against the current values on every read; an
/// <see cref="CheckRequest.Explain"/> request always evaluates live. <see cref="BatchCheckAsync"/>,
/// <see cref="ListObjectsAsync"/>, and <see cref="ListSubjectsAsync"/> delegate straight to the inner
/// engine, uncached.
/// </summary>
public sealed class ScopedCachingAuthorizer : IAuthorizer, ICustodexScopedCache
{
    private readonly IAuthorizer _inner;
    private readonly ISchemaStore? _schemaStore;
    private readonly ICacheStore? _cacheStore;
    private readonly CustodexCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly bool _enabled;

    private readonly ConcurrentDictionary<string, CacheSlot> _entries = new(StringComparer.Ordinal);
    private readonly Lock _snapshotGate = new();
    private readonly Dictionary<TenantContext, Snapshot> _snapshots = [];

    /// <summary>Creates a scoped caching decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The engine the decorator caches. Every miss, and every uncached operation, is delegated to it.</param>
    /// <param name="schemaStore">The active-schema source used to stamp and validate entries. When absent, the cache is disabled and every check evaluates live.</param>
    /// <param name="cacheStore">The tenant-epoch source used to invalidate entries on cross-scope writes. When absent, the cache is disabled and every check evaluates live.</param>
    /// <param name="options">The cache tuning. When <see cref="CustodexCacheOptions.Enabled"/> is <see langword="false"/> or <see cref="CustodexCacheOptions.Ttl"/> is not positive, the cache is disabled and every check evaluates live.</param>
    /// <param name="time">The clock the bounded TTL and the snapshot-refresh interval are measured against, using its monotonic timestamp.</param>
    public ScopedCachingAuthorizer(
        IAuthorizer inner,
        ISchemaStore? schemaStore,
        ICacheStore? cacheStore,
        CustodexCacheOptions options,
        TimeProvider time)
    {
        _inner = inner;
        _schemaStore = schemaStore;
        _cacheStore = cacheStore;
        _options = options;
        _time = time;
        _enabled = _options.Enabled
            && schemaStore is not null
            && cacheStore is not null
            && _options.Ttl > TimeSpan.Zero;
    }

    /// <inheritdoc />
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var (bypassCache, floorEpoch) = ConsistencyPolicy.Resolve(request.Context.Consistency, request.Tenant);

        if (!_enabled || request.Explain || bypassCache)
            return await _inner.CheckAsync(request, ct);

        var now = _time.GetTimestamp();
        var snapshot = await GetSnapshotAsync(request.Tenant, now, floorEpoch, ct);
        if (snapshot is null)
            return await _inner.CheckAsync(request, ct);

        if (floorEpoch is { } floor && snapshot.Epoch < floor)
            return await _inner.CheckAsync(request, ct);

        string? contextFingerprint = null;
        if (snapshot.ConditionCount > 0)
        {
            if (_options.Conditioned == ConditionedCaching.Skip)
                return await _inner.CheckAsync(request, ct);
            contextFingerprint = DecisionCacheKey.Fingerprint(request.Context);
        }

        var key = DecisionCacheKey.Build(
            request.Tenant, snapshot.SchemaVersion, request.Object, request.Permission, request.Subject, contextFingerprint);

        while (true)
        {
            var slot = _entries.GetOrAdd(key, _ => NewSlot(request, snapshot, key, now));
            if (IsFresh(slot, snapshot, now))
            {
                var joined = slot.Work.IsValueCreated;
                try
                {
                    var result = await slot.Work.Value.WaitAsync(ct);
                    if (joined)
                        CustodexDiagnostics.CacheHits.Add(1);
                    return result;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    _entries.TryRemove(new KeyValuePair<string, CacheSlot>(key, slot));
                    throw;
                }
            }

            CustodexDiagnostics.CacheSwept.Add(1);
            _entries.TryRemove(new KeyValuePair<string, CacheSlot>(key, slot));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        ConsistencyPolicy.Validate(request.Context.Consistency, request.Tenant);
        return _inner.BatchCheckAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        ConsistencyPolicy.Validate(request.Context.Consistency, request.Tenant);
        return _inner.ListObjectsAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        ConsistencyPolicy.Validate(request.Context.Consistency, request.Tenant);
        return _inner.ListSubjectsAsync(request, ct);
    }

    /// <inheritdoc />
    public void Clear() => _entries.Clear();

    /// <inheritdoc />
    public void InvalidateSubject(SubjectRef subject)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.Subject.Equals(subject))
                _entries.TryRemove(pair);
        }
    }

    /// <inheritdoc />
    public void InvalidateObject(EntityRef obj)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.Object.Equals(obj))
                _entries.TryRemove(pair);
        }
    }

    private CacheSlot NewSlot(CheckRequest request, Snapshot snapshot, string key, long createdTimestamp) =>
        new(
            snapshot.SchemaVersion,
            snapshot.Epoch,
            createdTimestamp,
            request.Object,
            request.Subject,
            new Lazy<Task<CheckResult>>(() => EvaluateAsync(request, key), LazyThreadSafetyMode.ExecutionAndPublication));

    private async Task<CheckResult> EvaluateAsync(CheckRequest request, string key)
    {
        CustodexDiagnostics.CacheMisses.Add(1);
        try
        {
            return await _inner.CheckAsync(request, CancellationToken.None);
        }
        catch
        {
            _entries.TryRemove(key, out _);
            throw;
        }
    }

    private bool IsFresh(CacheSlot slot, Snapshot snapshot, long now) =>
        slot.SchemaVersion == snapshot.SchemaVersion
            && slot.Epoch >= snapshot.Epoch
            && _time.GetElapsedTime(slot.CreatedTimestamp, now) < _options.Ttl;

    private async Task<Snapshot?> GetSnapshotAsync(TenantContext tenant, long now, long? floorEpoch, CancellationToken ct)
    {
        lock (_snapshotGate)
        {
            if (_snapshots.TryGetValue(tenant, out var cached)
                && _time.GetElapsedTime(cached.RefreshedTimestamp, now) < _options.EpochRefreshInterval
                && (floorEpoch is not { } floor || cached.Epoch >= floor))
                return cached;
        }

        var schema = await _schemaStore!.GetActiveAsync(tenant.Store, ct);
        if (schema is null)
            return null;

        var epoch = await _cacheStore!.GetEpochAsync(tenant, ct);
        var snapshot = new Snapshot(schema.Version, schema.Conditions.Count, epoch, now);
        lock (_snapshotGate)
            _snapshots[tenant] = snapshot;
        return snapshot;
    }

    private sealed record Snapshot(string SchemaVersion, int ConditionCount, long Epoch, long RefreshedTimestamp);

    private sealed class CacheSlot(
        string schemaVersion, long epoch, long createdTimestamp, EntityRef obj, SubjectRef subject,
        Lazy<Task<CheckResult>> work)
    {
        public string SchemaVersion { get; } = schemaVersion;

        public long Epoch { get; } = epoch;

        public long CreatedTimestamp { get; } = createdTimestamp;

        public EntityRef Object { get; } = obj;

        public SubjectRef Subject { get; } = subject;

        public Lazy<Task<CheckResult>> Work { get; } = work;
    }
}
