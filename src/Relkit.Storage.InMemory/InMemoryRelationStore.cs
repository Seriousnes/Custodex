using Relkit.Abstractions;

namespace Relkit.Storage.InMemory;

public sealed class InMemoryRelationStore : IRelationStore
{
    private readonly record struct Key(string Store, string Tenant);

    // Identity excludes ConditionRef.Parameters (reference-equality on the dictionary).
    private readonly record struct TupleIdentity(
        string ObjType, string ObjId, string Relation,
        string SubjType, string SubjId, string? SubjRelation, string? ConditionName);

    private readonly object _gate = new();
    private readonly Dictionary<Key, Dictionary<TupleIdentity, RelationTuple>> _data = new();

    private static Key KeyOf(TenantContext t) => new(t.Store, t.Tenant);

    private static TupleIdentity IdentityOf(RelationTuple tuple) => new(
        tuple.Object.Type, tuple.Object.Id, tuple.Relation,
        tuple.Subject.Type, tuple.Subject.Id, tuple.Subject.Relation, tuple.Condition?.Name);

    public Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(
        TenantContext t, EntityRef obj, string relation, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<RelationTuple> result =
                _data.TryGetValue(KeyOf(t), out var bucket)
                    ? bucket.Values.Where(x =>
                        string.Equals(x.Object.Type, obj.Type, StringComparison.Ordinal) &&
                        string.Equals(x.Object.Id, obj.Id, StringComparison.Ordinal) &&
                        string.Equals(x.Relation, relation, StringComparison.Ordinal)).ToList()
                    : [];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(
        TenantContext t, SubjectRef subject, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<RelationTuple> result =
                _data.TryGetValue(KeyOf(t), out var bucket)
                    ? bucket.Values.Where(x =>
                        string.Equals(x.Subject.Type, subject.Type, StringComparison.Ordinal) &&
                        string.Equals(x.Subject.Id, subject.Id, StringComparison.Ordinal) &&
                        string.Equals(x.Subject.Relation, subject.Relation, StringComparison.Ordinal)).ToList()
                    : [];
            return Task.FromResult(result);
        }
    }

    public Task WriteAsync(
        TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(KeyOf(t), out var bucket))
                _data[KeyOf(t)] = bucket = new Dictionary<TupleIdentity, RelationTuple>();

            foreach (var tuple in remove)
                bucket.Remove(IdentityOf(tuple));
            foreach (var tuple in add)
                bucket[IdentityOf(tuple)] = tuple;
        }
        return Task.CompletedTask;
    }
}
