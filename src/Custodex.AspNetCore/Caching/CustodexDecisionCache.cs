using System.Collections.Concurrent;

using Custodex.Abstractions;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class CustodexDecisionCache : ICustodexDecisionCache
{
    private readonly IAuthorizer _authorizer;
    private readonly ISchemaStore? _schemaStore;
    private readonly ICacheStore? _cacheStore;
    private readonly DecisionCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly bool _enabled;
    private readonly long _ttlTicks;
    private readonly long _refreshTicks;

    private readonly ConcurrentDictionary<string, CacheSlot> _entries = new(StringComparer.Ordinal);
    private readonly Lock _snapshotGate = new();
    private readonly Dictionary<TenantContext, Snapshot> _snapshots = [];

    public CustodexDecisionCache(
        IAuthorizer authorizer,
        ISchemaStore? schemaStore,
        ICacheStore? cacheStore,
        IOptions<CustodexAuthorizationOptions> options,
        TimeProvider time)
    {
        _authorizer = authorizer;
        _schemaStore = schemaStore;
        _cacheStore = cacheStore;
        _options = options.Value.DecisionCache;
        _time = time;
        _enabled = _options.Enabled && schemaStore is not null && cacheStore is not null;
        _ttlTicks = _options.Ttl.Ticks;
        _refreshTicks = _options.EpochRefreshInterval.Ticks;
    }

    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        if (!_enabled || request.Explain)
            return await _authorizer.CheckAsync(request, ct);

        var snapshot = await GetSnapshotAsync(request.Tenant, ct);
        if (snapshot is null)
            return await _authorizer.CheckAsync(request, ct);

        string? contextFingerprint = null;
        if (snapshot.ConditionCount > 0)
        {
            if (_options.Conditioned == ConditionedCaching.Skip)
                return await _authorizer.CheckAsync(request, ct);
            contextFingerprint = DecisionCacheKey.Fingerprint(request.Context);
        }

        var key = DecisionCacheKey.Build(
            request.Tenant, snapshot.SchemaVersion, request.Object, request.Permission, request.Subject, contextFingerprint);
        var nowTicks = _time.GetUtcNow().UtcTicks;

        while (true)
        {
            var slot = _entries.GetOrAdd(key, _ => NewSlot(request, snapshot, key, nowTicks));
            if (IsFresh(slot, snapshot, nowTicks))
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

    public void Clear() => _entries.Clear();

    public void InvalidateSubject(SubjectRef subject)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.Subject.Equals(subject))
                _entries.TryRemove(pair);
        }
    }

    public void InvalidateObject(EntityRef obj)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.Object.Equals(obj))
                _entries.TryRemove(pair);
        }
    }

    private CacheSlot NewSlot(CheckRequest request, Snapshot snapshot, string key, long nowTicks) =>
        new(
            snapshot.SchemaVersion,
            snapshot.Epoch,
            nowTicks + _ttlTicks,
            request.Object,
            request.Subject,
            new Lazy<Task<CheckResult>>(() => EvaluateAsync(request, key), LazyThreadSafetyMode.ExecutionAndPublication));

    private async Task<CheckResult> EvaluateAsync(CheckRequest request, string key)
    {
        CustodexDiagnostics.CacheMisses.Add(1);
        try
        {
            return await _authorizer.CheckAsync(request, CancellationToken.None);
        }
        catch
        {
            _entries.TryRemove(key, out _);
            throw;
        }
    }

    private static bool IsFresh(CacheSlot slot, Snapshot snapshot, long nowTicks) =>
        slot.SchemaVersion == snapshot.SchemaVersion
            && slot.Epoch >= snapshot.Epoch
            && nowTicks < slot.ExpiresAtTicks;

    private async Task<Snapshot?> GetSnapshotAsync(TenantContext tenant, CancellationToken ct)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        lock (_snapshotGate)
        {
            if (_snapshots.TryGetValue(tenant, out var cached) && nowTicks - cached.RefreshedAtTicks < _refreshTicks)
                return cached;
        }

        var schema = await _schemaStore!.GetActiveAsync(tenant.Store, ct);
        if (schema is null)
            return null;

        var epoch = await _cacheStore!.GetEpochAsync(tenant, ct);
        var snapshot = new Snapshot(schema.Version, schema.Conditions.Count, epoch, nowTicks);
        lock (_snapshotGate)
            _snapshots[tenant] = snapshot;
        return snapshot;
    }

    private sealed record Snapshot(string SchemaVersion, int ConditionCount, long Epoch, long RefreshedAtTicks);

    private sealed class CacheSlot(
        string schemaVersion, long epoch, long expiresAtTicks, EntityRef obj, SubjectRef subject,
        Lazy<Task<CheckResult>> work)
    {
        public string SchemaVersion { get; } = schemaVersion;

        public long Epoch { get; } = epoch;

        public long ExpiresAtTicks { get; } = expiresAtTicks;

        public EntityRef Object { get; } = obj;

        public SubjectRef Subject { get; } = subject;

        public Lazy<Task<CheckResult>> Work { get; } = work;
    }
}
