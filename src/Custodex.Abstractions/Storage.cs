using System.Data.Common;

namespace Custodex.Abstractions;

/// <summary>
/// A storage transaction that batches writes so they commit atomically. Operations enlist their writes
/// on the unit of work; nothing is durable until <see cref="CommitAsync"/> succeeds, and disposing
/// without committing discards them.
/// </summary>
/// <remarks>
/// That lifetime describes a unit of work the engine owns, opened by <see cref="IUnitOfWorkFactory.BeginAsync"/>.
/// A unit of work that enlists in a host-owned connection and transaction, returned by
/// <see cref="IUnitOfWorkFactory.Enlist"/>, instead borrows the host's lifetime: its
/// <see cref="CommitAsync"/> and disposal are no-ops, and durability and rollback follow the host's
/// own commit or rollback of the transaction it supplied.
/// </remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>Commits every write enlisted on this unit of work atomically.</summary>
    /// <param name="ct">A token to cancel the commit.</param>
    Task CommitAsync(CancellationToken ct = default);
}

/// <summary>Opens <see cref="IUnitOfWork"/> instances — one per logical write transaction.</summary>
public interface IUnitOfWorkFactory
{
    /// <summary>Begins a new unit of work.</summary>
    /// <param name="ct">A token to cancel starting the transaction.</param>
    /// <returns>An open unit of work to enlist writes on.</returns>
    Task<IUnitOfWork> BeginAsync(CancellationToken ct = default);

    /// <summary>
    /// Enlists in a unit of work over a connection and transaction the consuming application already
    /// owns, so engine writes commit atomically inside the host's transaction. The engine never
    /// commits, rolls back, or closes either handle — the host keeps full control of their lifetime,
    /// and the returned unit of work's <see cref="IUnitOfWork.CommitAsync"/> is a no-op.
    /// </summary>
    /// <param name="connection">The host-owned, open connection to borrow.</param>
    /// <param name="transaction">The host-owned transaction the engine writes enlist on.</param>
    /// <returns>A unit of work whose enlisted writes participate in the host's transaction.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown by factories that cannot fold engine writes into a host-owned transaction, such as an
    /// in-memory provider with no real transaction or an out-of-process client.
    /// </exception>
    IUnitOfWork Enlist(DbConnection connection, DbTransaction transaction) =>
        throw new NotSupportedException(
            "This unit-of-work factory cannot enlist in a host-supplied connection and transaction.");
}

/// <summary>
/// The relation-tuple store: the indexed reads the engine traverses during evaluation, and the
/// transactional write path. Every operation is scoped to a <see cref="TenantContext"/>.
/// </summary>
public interface IRelationStore
{
    /// <summary>Reads the tuples on an object for one relation (the forward edge lookup).</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="obj">The object to read edges from.</param>
    /// <param name="relation">The relation to read.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(TenantContext t, EntityRef obj, string relation, CancellationToken ct = default);

    /// <summary>Reads the tuples naming a subject (the reverse edge lookup).</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="subject">The subject to find edges for.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(TenantContext t, SubjectRef subject, CancellationToken ct = default);

    /// <summary>Lists the ids of every stored object of a type (the candidate set for enumeration).</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="objectType">The entity type to enumerate.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<string>> ListObjectIdsAsync(TenantContext t, string objectType, CancellationToken ct = default);

    /// <summary>Adds and removes tuples atomically, enlisted on a unit of work.</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="add">The tuples to insert.</param>
    /// <param name="remove">The tuples to delete.</param>
    /// <param name="uow">The unit of work the change commits with.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task WriteAsync(TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove, IUnitOfWork uow, CancellationToken ct = default);
}

/// <summary>Stores the active <see cref="Schema"/> per store.</summary>
public interface ISchemaStore
{
    /// <summary>Reads the active schema for a store, or <see langword="null"/> when none is set.</summary>
    /// <param name="store">The store identifier.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default);

    /// <summary>Sets the active schema for a store, enlisted on a unit of work.</summary>
    /// <param name="store">The store identifier.</param>
    /// <param name="schema">The schema to activate.</param>
    /// <param name="uow">The unit of work the change commits with.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default);
}

/// <summary>Stores per-object attribute bags read by condition (ABAC) evaluation.</summary>
public interface IAttributeStore
{
    /// <summary>Reads an object's attributes, or <see langword="null"/> when none are stored.</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="obj">The object whose attributes are read.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyDictionary<string, object?>?> GetAsync(TenantContext t, EntityRef obj, CancellationToken ct = default);

    /// <summary>Replaces an object's attributes, enlisted on a unit of work.</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="obj">The object whose attributes are set.</param>
    /// <param name="attrs">The attribute values to store.</param>
    /// <param name="uow">The unit of work the change commits with.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetAsync(TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs, IUnitOfWork uow, CancellationToken ct = default);
}

/// <summary>Persistent reverse index: maps (subject, permission, objectType) to the set of objectIds the subject holds that permission on.</summary>
public interface IIndexStore
{
    /// <summary>Insert-or-update each row on its natural key, setting <c>conditioned</c>.</summary>
    Task UpsertAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>Remove every index row for one object (the unit incremental maintenance recomputes).</summary>
    Task DeleteForObjectAsync(TenantContext t, string schemaVersion, string objectType, string objectId,
        IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>Remove specific rows identified by their natural key (targeted incremental diffs).</summary>
    Task DeleteRowsAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>
    /// The ListObjects scan: rows for one (subject, permission, objectType), ordinal-sorted by
    /// object_id, starting strictly after <paramref name="afterObjectId"/> (null = from the start),
    /// capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ReverseIndexRow>> QueryObjectsAsync(TenantContext t, string schemaVersion,
        string subject, string permission, string objectType, int limit, string? afterObjectId,
        CancellationToken ct = default);

    /// <summary>Every index row for one object (the "current rows" side of an incremental diff).</summary>
    Task<IReadOnlyList<ReverseIndexRow>> ReadForObjectAsync(TenantContext t, string schemaVersion,
        string objectType, string objectId, CancellationToken ct = default);

    /// <summary>Drop all index rows for a (store, tenant) across every schema_version (rebuild or schema-change invalidation).</summary>
    Task ClearAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>Has the index been built and marked current for this (store, tenant, schema_version)?</summary>
    Task<bool> IsBuiltAsync(TenantContext t, string schemaVersion, CancellationToken ct = default);

    /// <summary>Record that the index is current for this (store, tenant, schema_version).</summary>
    Task MarkBuiltAsync(TenantContext t, string schemaVersion, IUnitOfWork uow, CancellationToken ct = default);
}

/// <summary>A cached decision payload tagged with the tenant epoch it was computed under.</summary>
/// <param name="Value">The serialized cached value.</param>
/// <param name="Epoch">The tenant epoch the value was computed at; a later epoch makes it stale.</param>
public sealed record CacheEntry(byte[] Value, long Epoch);

/// <summary>
/// A decision cache keyed by request, invalidated coarsely by a per-tenant epoch counter that each write
/// bumps. An entry computed under an older epoch is treated as stale.
/// </summary>
public interface ICacheStore
{
    /// <summary>Reads a cached entry by key, or <see langword="null"/> on a miss.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Stores an entry under a key with a time-to-live.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The entry to cache.</param>
    /// <param name="ttl">How long the entry may live before expiring.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Reads the current epoch for a tenant; entries cached under an earlier epoch are stale.</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default);

    /// <summary>Advances a tenant's epoch, invalidating every entry cached under the previous one.</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="uow">The unit of work the bump commits with.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The epoch the tenant advanced to.</returns>
    Task<long> BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default);
}

/// <summary>Append-only audit of data changes, queried for history and change feeds.</summary>
public interface IChangeLogStore
{
    /// <summary>Appends one change entry, enlisted on a unit of work.</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="entry">The change to record.</param>
    /// <param name="uow">The unit of work the append commits with.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The identifier the store assigned to the appended entry.</returns>
    Task<long> AppendAsync(TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>Reads change-log entries matching a filter.</summary>
    /// <param name="t">The tenant scope.</param>
    /// <param name="filter">The query constraints (time range, actor, limit).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(TenantContext t, ChangeLogFilter filter, CancellationToken ct = default);
}
