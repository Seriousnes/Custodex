namespace Relkit.Abstractions;

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

public interface IIndexStore { }   // populated in M2

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
