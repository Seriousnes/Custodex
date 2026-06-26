using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

/// <summary>An in-memory <see cref="ISchemaStore"/>: holds the active schema per store in process memory for tests and local development. Thread-safe; data lives only for the lifetime of the process.</summary>
public sealed class InMemorySchemaStore : ISchemaStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Schema> _active = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_active.TryGetValue(store, out var schema) ? schema : null);
    }

    /// <inheritdoc/>
    public Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
            _active[store] = schema;
        return Task.CompletedTask;
    }
}
