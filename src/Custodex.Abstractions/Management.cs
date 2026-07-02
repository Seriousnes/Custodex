namespace Custodex.Abstractions;

/// <summary>Constrains a tuple read: each non-null member must match exactly, and a null member matches anything.</summary>
/// <param name="ObjectType">Restrict to this object type when set.</param>
/// <param name="ObjectId">Restrict to this object id when set.</param>
/// <param name="Relation">Restrict to this relation when set.</param>
/// <param name="SubjectType">Restrict to this subject type when set.</param>
/// <param name="SubjectId">Restrict to this subject id when set.</param>
public sealed record TupleFilter(string? ObjectType = null, string? ObjectId = null, string? Relation = null,
    string? SubjectType = null, string? SubjectId = null);

/// <summary>Constrains a change-log read.</summary>
/// <param name="Since">Return only entries at or after this instant when set.</param>
/// <param name="Actor">Restrict to changes made by this actor when set.</param>
/// <param name="Limit">The maximum number of entries to return.</param>
public sealed record ChangeLogFilter(DateTimeOffset? Since = null, string? Actor = null, int Limit = 100);

/// <summary>One recorded data change in the audit log.</summary>
/// <param name="Id">The monotonic entry identifier.</param>
/// <param name="Actor">The actor credited with the change.</param>
/// <param name="Operation">The kind of change recorded.</param>
/// <param name="Target">What the change applied to.</param>
/// <param name="Before">The prior state, when applicable.</param>
/// <param name="After">The new state, when applicable.</param>
/// <param name="OccurredAt">When the change was recorded.</param>
public sealed record ChangeLogEntry(long Id, string Actor, string Operation, string Target,
    object? Before, object? After, DateTimeOffset OccurredAt);

/// <summary>
/// The write-side API for tenant data: editing tuples and attributes and reading them back. Writes
/// record the responsible actor to the change log. This is the surface a consuming app drives to keep
/// authorization data current at runtime, with no engineers in the loop.
/// </summary>
public interface IRelationManager
{
    /// <summary>Writes (inserts) tuples, attributing the change to an actor.</summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="actor">The actor credited in the change log.</param>
    /// <param name="tuples">The tuples to write.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ConsistencyToken"/> a caller can replay on a later read to observe this write.</returns>
    Task<ConsistencyToken> WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);

    /// <summary>Deletes tuples, attributing the change to an actor.</summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="actor">The actor credited in the change log.</param>
    /// <param name="tuples">The tuples to delete.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ConsistencyToken"/> a caller can replay on a later read to observe this write.</returns>
    Task<ConsistencyToken> DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);

    /// <summary>Replaces an object's attributes, attributing the change to an actor.</summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="actor">The actor credited in the change log.</param>
    /// <param name="obj">The object whose attributes are written.</param>
    /// <param name="attributes">The attribute values to store.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ConsistencyToken"/> a caller can replay on a later read to observe this write.</returns>
    Task<ConsistencyToken> WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default);

    /// <summary>
    /// Writes (inserts) tuples on a host-supplied unit of work, attributing the change to an actor.
    /// The change becomes durable only when the host commits the unit of work; this manager neither
    /// commits nor disposes it.
    /// </summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="actor">The actor credited in the change log.</param>
    /// <param name="tuples">The tuples to write.</param>
    /// <param name="uow">The host-supplied unit of work the change enlists on.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ConsistencyToken"/> a caller can replay on a later read to observe this write.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown by managers that cannot enlist in a host-supplied unit of work, such as an out-of-process client.
    /// </exception>
    Task<ConsistencyToken> WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, IUnitOfWork uow, CancellationToken ct = default) =>
        throw new NotSupportedException("This relation manager cannot enlist writes in a host-supplied unit of work.");

    /// <summary>
    /// Deletes tuples on a host-supplied unit of work, attributing the change to an actor. The change
    /// becomes durable only when the host commits the unit of work; this manager neither commits nor
    /// disposes it.
    /// </summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="actor">The actor credited in the change log.</param>
    /// <param name="tuples">The tuples to delete.</param>
    /// <param name="uow">The host-supplied unit of work the change enlists on.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ConsistencyToken"/> a caller can replay on a later read to observe this write.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown by managers that cannot enlist in a host-supplied unit of work, such as an out-of-process client.
    /// </exception>
    Task<ConsistencyToken> DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, IUnitOfWork uow, CancellationToken ct = default) =>
        throw new NotSupportedException("This relation manager cannot enlist writes in a host-supplied unit of work.");

    /// <summary>
    /// Replaces an object's attributes on a host-supplied unit of work, attributing the change to an
    /// actor. The change becomes durable only when the host commits the unit of work; this manager
    /// neither commits nor disposes it.
    /// </summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="actor">The actor credited in the change log.</param>
    /// <param name="obj">The object whose attributes are written.</param>
    /// <param name="attributes">The attribute values to store.</param>
    /// <param name="uow">The host-supplied unit of work the change enlists on.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ConsistencyToken"/> a caller can replay on a later read to observe this write.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown by managers that cannot enlist in a host-supplied unit of work, such as an out-of-process client.
    /// </exception>
    Task<ConsistencyToken> WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, IUnitOfWork uow, CancellationToken ct = default) =>
        throw new NotSupportedException("This relation manager cannot enlist writes in a host-supplied unit of work.");

    /// <summary>Reads tuples matching a filter.</summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="filter">The read constraints.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(TenantContext tenant, TupleFilter filter, CancellationToken ct = default);

    /// <summary>Reads change-log entries matching a filter.</summary>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="filter">The read constraints.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default);
}

/// <summary>The API for validating and activating a store's <see cref="Schema"/>.</summary>
public interface ISchemaManager
{
    /// <summary>Validates a schema without activating it.</summary>
    /// <param name="schema">The schema to check.</param>
    /// <returns>The validation outcome and any error messages.</returns>
    SchemaValidationResult ValidateSchema(Schema schema);

    /// <summary>Validates and activates a schema for a store.</summary>
    /// <param name="store">The store identifier.</param>
    /// <param name="schema">The schema to activate.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default);

    /// <summary>
    /// Validates and activates a schema for a store on a host-supplied unit of work. The activation
    /// becomes durable only when the host commits the unit of work; this manager neither commits nor
    /// disposes it. Validation runs before any write, so an invalid schema throws without enlisting.
    /// </summary>
    /// <param name="store">The store identifier.</param>
    /// <param name="schema">The schema to activate.</param>
    /// <param name="uow">The host-supplied unit of work the activation enlists on.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown by managers that cannot enlist in a host-supplied unit of work, such as an out-of-process client.
    /// </exception>
    Task SetActiveSchemaAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default) =>
        throw new NotSupportedException("This schema manager cannot enlist activation in a host-supplied unit of work.");

    /// <summary>Reads the active schema for a store, or <see langword="null"/> when none is set.</summary>
    /// <param name="store">The store identifier.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default);
}

/// <summary>Provisions stores — the top-level isolation boundary that owns a schema and its data.</summary>
public interface IStoreManager
{
    /// <summary>Creates a store.</summary>
    /// <param name="store">The store identifier to create.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task CreateStoreAsync(string store, CancellationToken ct = default);
}

/// <summary>Provisions tenants within a store.</summary>
public interface ITenantManager
{
    /// <summary>Creates a tenant within a store.</summary>
    /// <param name="tenant">The store and tenant to create.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default);
}
