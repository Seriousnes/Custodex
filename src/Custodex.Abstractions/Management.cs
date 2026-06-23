namespace Custodex.Abstractions;

public sealed record TupleFilter(string? ObjectType = null, string? ObjectId = null, string? Relation = null,
    string? SubjectType = null, string? SubjectId = null);
public sealed record ChangeLogFilter(DateTimeOffset? Since = null, string? Actor = null, int Limit = 100);
public sealed record ChangeLogEntry(long Id, string Actor, string Operation, string Target,
    object? Before, object? After, DateTimeOffset OccurredAt);

public interface IRelationManager
{
    Task WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default);
    Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(TenantContext tenant, TupleFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default);
}

public interface ISchemaManager
{
    SchemaValidationResult ValidateSchema(Schema schema);
    Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default);
    Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default);
}

public interface IStoreManager { Task CreateStoreAsync(string store, CancellationToken ct = default); }
public interface ITenantManager { Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default); }
