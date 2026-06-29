using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.SqlServer.Index;

/// <summary>
/// Incrementally maintains the reverse index inside the caller's transaction on each tuple write.
/// Computes the closure of objects affected by the changed tuples, then recomputes and replaces
/// each affected object's rows atomically with the write. Because recompute re-derives the full
/// truth from the current tuple state, it handles exclusion, multi-path grants, and arrow traversal
/// without delta arithmetic. A stale or unbuilt index is skipped; the write still commits and a
/// full rebuild is required before the index is used again.
/// </summary>
public sealed class ReverseIndexMaintainer(
    ISchemaStore schemas,
    SqlServerRelationStore relations,
    IAttributeStore attributes,
    IIndexStore index)
{
    /// <summary>
    /// Updates the reverse index for all objects transitively affected by <paramref name="changed"/>,
    /// executing all index writes within <paramref name="uow"/> so they commit atomically with the
    /// tuple write. Returns immediately when <paramref name="changed"/> is empty, when no active
    /// schema exists, or when the index has not been built for the active schema version.
    /// </summary>
    public async Task MaintainAsync(
        TenantContext t, IReadOnlyList<RelationTuple> changed, IUnitOfWork uow, CancellationToken ct = default)
    {
        if (changed.Count == 0) return;

        var schema = await schemas.GetActiveAsync(t.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{t.Store}'>");

        if (!await index.IsBuiltAsync(t, schema.Version, ct)) return;

        var schemaIndex = new SchemaIndex(schema);
        var w = SqlServerUnitOfWork.From(uow);

        var affected = await AffectedClosure.ComputeAsync(w.Connection, w.Transaction, t, changed, ct);
        if (affected.Count == 0) return;

        var inputs = await RebuildEnumeration.LoadAsync(w.Connection, w.Transaction, t, ct);

        var bound = relations.OnUnitOfWork(uow);
        var recomputer = new ObjectRowRecomputer(bound, attributes);

        foreach (var obj in affected)
        {
            if (!schemaIndex.TryType(obj.Type, out _))
            {
                await index.DeleteForObjectAsync(t, schema.Version, obj.Type, obj.Id, uow, ct);
                continue;
            }

            var rows = await recomputer.RecomputeAsync(schemaIndex, schemas, t, obj, inputs.Users, ct);
            await index.DeleteForObjectAsync(t, schema.Version, obj.Type, obj.Id, uow, ct);
            await index.UpsertAsync(t, schema.Version, rows, uow, ct);
        }
    }
}
