using Custodex.Abstractions;

namespace Custodex.Storage.Postgres.Index;

/// <summary>
/// Wraps <see cref="AuditedWritePath"/> so every tuple write also maintains the reverse index
/// on the same unit of work, ensuring tuple data and index rows commit or roll back atomically.
/// A schema change clears the index for the old version and leaves the new version unbuilt,
/// so a stale index is never served.
/// </summary>
public sealed class IndexedWritePath(
    AuditedWritePath inner,
    ReverseIndexMaintainer maintainer,
    IIndexStore index)
{
    /// <summary>
    /// Writes tuples via the inner path and then updates the reverse index for all
    /// objects affected by <paramref name="add"/> and <paramref name="remove"/>,
    /// all within <paramref name="uow"/>.
    /// </summary>
    public async Task WriteTuplesAsync(
        TenantContext t, string actor,
        IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await inner.WriteTuplesAsync(t, actor, add, remove, uow, ct);
        var changed = new List<RelationTuple>(add.Count + remove.Count);
        changed.AddRange(add);
        changed.AddRange(remove);
        await maintainer.MaintainAsync(t, changed, uow, ct);
    }

    /// <summary>
    /// Writes attributes via the inner path and then triggers a recompute for the
    /// affected object so that conditioned index rows reflect the new attribute values,
    /// all within <paramref name="uow"/>.
    /// </summary>
    public async Task WriteAttributesAsync(
        TenantContext t, string actor, EntityRef obj,
        IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await inner.WriteAttributesAsync(t, actor, obj, before, after, uow, ct);
        await maintainer.MaintainAsync(t, [new RelationTuple(obj, "*attributes*", new SubjectRef(obj.Type, obj.Id))], uow, ct);
    }

    /// <summary>
    /// Sets the schema via the inner path and then clears the entire reverse index so that
    /// rows stamped with the old schema version are removed. The new version has no built marker,
    /// so index reads fall back until a full rebuild runs.
    /// </summary>
    public async Task SetSchemaAsync(
        string store, TenantContext t, string actor, Schema schema,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await inner.SetSchemaAsync(store, t, actor, schema, uow, ct);
        await index.ClearAsync(t, uow, ct);
    }
}
