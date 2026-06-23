using Relkit.Abstractions;

namespace Relkit.Storage.InMemory;

public sealed class InMemoryAttributeStore : IAttributeStore
{
    private readonly record struct Key(string Store, string Tenant, string ObjType, string ObjId);

    private readonly object _gate = new();
    private readonly Dictionary<Key, Dictionary<string, object?>> _data = new();

    private static Key KeyOf(TenantContext t, EntityRef obj) => new(t.Store, t.Tenant, obj.Type, obj.Id);

    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        TenantContext t, EntityRef obj, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(KeyOf(t, obj), out var bag))
                return Task.FromResult<IReadOnlyDictionary<string, object?>?>(null);
            IReadOnlyDictionary<string, object?> copy =
                new Dictionary<string, object?>(bag, StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, object?>?>(copy);
        }
    }

    public Task SetAsync(
        TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
            _data[KeyOf(t, obj)] = new Dictionary<string, object?>(attrs, StringComparer.Ordinal);
        return Task.CompletedTask;
    }
}
