using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

public sealed class InMemorySchemaStore : ISchemaStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Schema> _active = new(StringComparer.Ordinal);

    public Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_active.TryGetValue(store, out var schema) ? schema : null);
    }

    public Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
            _active[store] = schema;
        return Task.CompletedTask;
    }
}
