namespace Custodex.Service.Rest;

/// <summary>Identifies a condition applied to a relation tuple.</summary>
public sealed record ConditionRefDto(string Name, Dictionary<string, object?> Parameters);

/// <summary>A relation tuple with an optional condition.</summary>
public sealed record RelationTupleDto(
    EntityRefDto Object,
    string Relation,
    SubjectRefDto Subject,
    ConditionRefDto? Condition);

/// <summary>
/// Request body for writing relation tuples. The <c>Actor</c> field is ignored for audit attribution;
/// the persisted change-log actor is derived server-side from the authenticated caller.
/// </summary>
public sealed record WriteTuplesRequestDto(
    string Store,
    string Tenant,
    string Actor,
    IReadOnlyList<RelationTupleDto> Tuples);

/// <summary>
/// Request body for deleting relation tuples. The <c>Actor</c> field is ignored for audit attribution;
/// the persisted change-log actor is derived server-side from the authenticated caller.
/// </summary>
public sealed record DeleteTuplesRequestDto(
    string Store,
    string Tenant,
    string Actor,
    IReadOnlyList<RelationTupleDto> Tuples);

/// <summary>
/// Request body for writing attributes onto an entity. The <c>Actor</c> field is ignored for audit
/// attribution; the persisted change-log actor is derived server-side from the authenticated caller.
/// </summary>
public sealed record WriteAttributesRequestDto(
    string Store,
    string Tenant,
    string Actor,
    EntityRefDto Object,
    Dictionary<string, object?> Attributes);

/// <summary>Request body for reading relation tuples by filter.</summary>
public sealed record ReadTuplesRequestDto(
    string Store,
    string Tenant,
    string? ObjectType = null,
    string? ObjectId = null,
    string? Relation = null,
    string? SubjectType = null,
    string? SubjectId = null);

/// <summary>Response for reading relation tuples.</summary>
public sealed record ReadTuplesResponseDto(IReadOnlyList<RelationTupleDto> Tuples);

/// <summary>One entry in the change-log.</summary>
public sealed record ChangeLogEntryDto(
    long Id,
    string Actor,
    string Operation,
    string Target,
    object? Before,
    object? After,
    DateTimeOffset OccurredAt);

/// <summary>Request body for reading the change log.</summary>
public sealed record ReadChangeLogRequestDto(
    string Store,
    string Tenant,
    DateTimeOffset? Since = null,
    string? Actor = null,
    int Limit = 100);

/// <summary>Response for reading the change log.</summary>
public sealed record ReadChangeLogResponseDto(IReadOnlyList<ChangeLogEntryDto> Entries);

/// <summary>Request body for validating a schema without activating it.</summary>
public sealed record ValidateSchemaRequestDto(string SchemaJson);

/// <summary>Result of schema validation.</summary>
public sealed record ValidateSchemaResponseDto(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>Request body for activating a schema for a store.</summary>
public sealed record SetActiveSchemaRequestDto(string SchemaJson);

/// <summary>Response for fetching a store's active schema.</summary>
public sealed record GetActiveSchemaResponseDto(bool Found, string? SchemaJson);

/// <summary>Request body for provisioning a store.</summary>
public sealed record CreateStoreRequestDto(string Store);

/// <summary>Request body for provisioning a tenant.</summary>
public sealed record CreateTenantRequestDto(string Store, string Tenant);
