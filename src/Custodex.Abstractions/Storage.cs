namespace Custodex.Abstractions;

public interface IUnitOfWork : IAsyncDisposable { Task CommitAsync(CancellationToken ct = default); }
public interface IUnitOfWorkFactory { Task<IUnitOfWork> BeginAsync(CancellationToken ct = default); }

public interface IRelationStore
{
    Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(TenantContext t, EntityRef obj, string relation, CancellationToken ct = default);
    Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(TenantContext t, SubjectRef subject, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListObjectIdsAsync(TenantContext t, string objectType, CancellationToken ct = default);
    Task WriteAsync(TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove, IUnitOfWork uow, CancellationToken ct = default);
}

public interface ISchemaStore
{
    Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default);
    Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default);
}

public interface IAttributeStore
{
    Task<IReadOnlyDictionary<string, object?>?> GetAsync(TenantContext t, EntityRef obj, CancellationToken ct = default);
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

public sealed record CacheEntry(byte[] Value, long Epoch);
public interface ICacheStore
{
    Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default);
    Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default);
    Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default);
}

public interface IChangeLogStore
{
    Task AppendAsync(TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(TenantContext t, ChangeLogFilter filter, CancellationToken ct = default);
}
