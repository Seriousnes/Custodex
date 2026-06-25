using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

/// <summary>An in-memory <see cref="ICacheStore"/>: holds cached decisions and per-tenant epoch counters in process memory for tests and local development. Thread-safe; data lives only for the lifetime of the process, and expired entries are evicted lazily on read.</summary>
/// <param name="timeProvider">The clock used to apply entry time-to-live and expiry. Defaults to <see cref="TimeProvider.System"/>; pass a fake provider to control time in tests.</param>
public sealed class InMemoryCacheStore(TimeProvider? timeProvider = null) : ICacheStore
{
    private readonly record struct Key(string Store, string Tenant);
    private sealed record Slot(CacheEntry Entry, DateTimeOffset ExpiresAt);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<Key, long> _epochs = new();

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_gate)
            _entries[key] = new Slot(entry, _clock.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_epochs.TryGetValue(new Key(t.Store, t.Tenant), out var e) ? e : 0);
    }

    /// <inheritdoc/>
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
