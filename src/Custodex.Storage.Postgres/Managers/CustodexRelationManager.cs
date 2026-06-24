using Custodex.Abstractions;

namespace Custodex.Storage.Postgres.Managers;

/// <summary>
/// Concrete <see cref="IRelationManager"/> backed by Postgres. Each write call supplies the
/// caller's actor and before/after images, then sequences the data write, change log entry,
/// and epoch bump through <see cref="AuditedWritePath"/> in a single owned unit of work.
/// The engine never invents the actor — it is always caller-supplied.
/// </summary>
public sealed class CustodexRelationManager(
    NpgsqlUnitOfWorkFactory uowFactory,
    NpgsqlRelationStore relations,
    IAttributeStore attributes,
    NpgsqlChangeLogStore changeLog,
    AuditedWritePath audited) : IRelationManager
{
    /// <inheritdoc />
    public async Task WriteTuplesAsync(
        TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.WriteTuplesAsync(tenant, actor, tuples, [], uow, ct);
        await uow.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteTuplesAsync(
        TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.WriteTuplesAsync(tenant, actor, [], tuples, uow, ct);
        await uow.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task WriteAttributesAsync(
        TenantContext tenant, string actor, EntityRef obj,
        IReadOnlyDictionary<string, object?> attrs, CancellationToken ct = default)
    {
        var before = await attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.WriteAttributesAsync(tenant, actor, obj, before, attrs, uow, ct);
        await uow.CommitAsync(ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(
        TenantContext tenant, TupleFilter filter, CancellationToken ct = default)
        => relations.QueryAsync(tenant, filter, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(
        TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default)
        => changeLog.ReadAsync(tenant, filter, ct);
}
