using Relkit.Abstractions;

namespace Relkit.Storage.InMemory;

public sealed class InMemoryCacheStore(TimeProvider? timeProvider = null) : ICacheStore
{
    private readonly record struct Key(string Store, string Tenant);
    private sealed record Slot(CacheEntry Entry, DateTimeOffset ExpiresAt);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<Key, long> _epochs = new();

    public Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var slot))
                return Task.FromResult<CacheEntry?>(null);
            if (_clock.GetUtcNow() >= slot.ExpiresAt)
            {
                _entries.Remove(key);
                return Task.FromResult<CacheEntry?>(null);
            }
            return Task.FromResult<CacheEntry?>(slot.Entry);
        }
    }

    public Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_gate)
            _entries[key] = new Slot(entry, _clock.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }

    public Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_epochs.TryGetValue(new Key(t.Store, t.Tenant), out var e) ? e : 0);
    }

    public Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = new Key(t.Store, t.Tenant);
            _epochs[key] = (_epochs.TryGetValue(key, out var e) ? e : 0) + 1;
        }
        return Task.CompletedTask;
    }
}
