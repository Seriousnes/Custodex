using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

/// <summary>An in-memory <see cref="IChangeLogStore"/>: holds the append-only change log in process memory for tests and local development. Thread-safe; data lives only for the lifetime of the process.</summary>
public sealed class InMemoryChangeLogStore : IChangeLogStore
{
    private readonly record struct Key(string Store, string Tenant);

    private readonly object _gate = new();
    private readonly Dictionary<Key, List<ChangeLogEntry>> _data = new();
    private readonly Dictionary<Key, long> _nextId = new();

    private static Key KeyOf(TenantContext t) => new(t.Store, t.Tenant);

    /// <inheritdoc/>
    public Task AppendAsync(TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = KeyOf(t);
            if (!_data.TryGetValue(key, out var list))
            {
                _data[key] = list = [];
                _nextId[key] = 1;
            }
            var id = _nextId[key]++;
            list.Add(entry with { Id = id });
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(
        TenantContext t, ChangeLogFilter filter, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<ChangeLogEntry> query =
                _data.TryGetValue(KeyOf(t), out var list) ? list : [];

            if (filter.Since is { } since)
                query = query.Where(e => e.OccurredAt >= since);
            if (filter.Actor is { } actor)
                query = query.Where(e => string.Equals(e.Actor, actor, StringComparison.Ordinal));

            IReadOnlyList<ChangeLogEntry> result = query
                .OrderByDescending(e => e.Id)
                .Take(filter.Limit)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
