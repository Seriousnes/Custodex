using Custodex.Abstractions;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Sequences a data write, its <c>change_log</c> entries, and the cache-epoch bump on a single
/// <see cref="IUnitOfWork"/>, so all three commit or roll back together.
/// The caller supplies the actor and before/after images and owns commit/rollback on the unit of work.
/// </summary>
public sealed class AuditedWritePath(
    IRelationStore relations,
    IAttributeStore attributes,
    ISchemaStore schemas,
    IChangeLogStore changeLog,
    ICacheStore cache)
{
    /// <summary>
    /// Writes the given tuples, appends a <c>change_log</c> entry per affected tuple, and bumps
    /// the cache epoch — all on <paramref name="uow"/>.
    /// </summary>
    public async Task<ConsistencyToken> WriteTuplesAsync(
        TenantContext t, string actor,
        IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await relations.WriteAsync(t, add, remove, uow, ct);

        long changeLogId = 0;
        foreach (var tuple in add)
            changeLogId = await changeLog.AppendAsync(t, AuditEntry(actor, "write", Target(tuple), before: null, after: tuple), uow, ct);
        foreach (var tuple in remove)
            changeLogId = await changeLog.AppendAsync(t, AuditEntry(actor, "delete", Target(tuple), before: tuple, after: null), uow, ct);

        var epoch = await cache.BumpEpochAsync(t, uow, ct);
        return ConsistencyToken.Create(t, epoch, changeLogId);
    }

    /// <summary>
    /// Writes the given attributes, appends a <c>change_log</c> entry, and bumps the cache epoch
    /// — all on <paramref name="uow"/>.
    /// </summary>
    public async Task<ConsistencyToken> WriteAttributesAsync(
        TenantContext t, string actor, EntityRef obj,
        IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await attributes.SetAsync(t, obj, after, uow, ct);
        var changeLogId = await changeLog.AppendAsync(t, AuditEntry(actor, "write", obj.ToString(), before, after), uow, ct);
        var epoch = await cache.BumpEpochAsync(t, uow, ct);
        return ConsistencyToken.Create(t, epoch, changeLogId);
    }

    /// <summary>
    /// Sets the active schema, appends a <c>change_log</c> entry, and bumps the cache epoch
    /// — all on <paramref name="uow"/>.
    /// </summary>
    public async Task SetSchemaAsync(
        string store, TenantContext t, string actor, Schema schema,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await schemas.SetActiveAsync(store, schema, uow, ct);
        await changeLog.AppendAsync(t,
            AuditEntry(actor, "schema", $"{store}@{schema.Version}", before: null, after: schema.Version), uow, ct);
        await cache.BumpEpochAsync(t, uow, ct);
    }

    private static ChangeLogEntry AuditEntry(string actor, string op, string target, object? before, object? after)
        => new(Id: 0, actor, op, target, before, after, OccurredAt: default);

    private static string Target(RelationTuple t)
    {
        var subject = t.Subject.Relation is null
            ? $"{t.Subject.Type}:{t.Subject.Id}"
            : $"{t.Subject.Type}:{t.Subject.Id}#{t.Subject.Relation}";
        return $"{t.Object.Type}:{t.Object.Id}#{t.Relation}@{subject}";
    }
}
