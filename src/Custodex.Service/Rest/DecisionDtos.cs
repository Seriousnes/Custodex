namespace Custodex.Service.Rest;

/// <summary>Identifies a specific entity by type and id.</summary>
public sealed record EntityRefDto(string Type, string Id);

/// <summary>Identifies a subject, optionally scoped to a subject-set via a relation.</summary>
public sealed record SubjectRefDto(string Type, string Id, string? Relation);

/// <summary>Per-request context: the acting subject, optional point-in-time, and condition attributes.</summary>
public sealed record RequestContextDto(
    SubjectRefDto Subject,
    DateTimeOffset? Now,
    Dictionary<string, object?>? Attributes);

/// <summary>Request body for a single authorization check.</summary>
public sealed record CheckRequestDto(
    string Store,
    string Tenant,
    EntityRefDto Object,
    string Permission,
    RequestContextDto Context,
    bool Explain = false);

/// <summary>Result of a single authorization check.</summary>
public sealed record CheckResponseDto(bool Allowed, ExplainNodeDto? Explain);

/// <summary>One item in a batch-check request.</summary>
public sealed record CheckItemDto(EntityRefDto Object, string Permission);

/// <summary>Request body for a batch of authorization checks sharing one context.</summary>
public sealed record BatchCheckRequestDto(
    string Store,
    string Tenant,
    IReadOnlyList<CheckItemDto> Checks,
    RequestContextDto Context,
    bool Explain = false);

/// <summary>Result of a batch-check — one entry per input item, in order.</summary>
public sealed record BatchCheckResponseDto(IReadOnlyList<CheckResponseDto> Results);

/// <summary>Request body for listing objects a subject may act on.</summary>
public sealed record ListObjectsRequestDto(
    string Store,
    string Tenant,
    string ObjectType,
    string Permission,
    RequestContextDto Context,
    int PageSize = 50,
    string? ContinuationToken = null);

/// <summary>Result of a list-objects query — a page of matching object ids.</summary>
public sealed record ListObjectsResponseDto(
    IReadOnlyList<string> ObjectIds,
    string? ContinuationToken);

/// <summary>Request body for listing subjects who may act on an object.</summary>
public sealed record ListSubjectsRequestDto(
    string Store,
    string Tenant,
    EntityRefDto Object,
    string Permission,
    RequestContextDto Context,
    int PageSize = 50,
    string? ContinuationToken = null);

/// <summary>Result of a list-subjects query — a page of matching subject references.</summary>
public sealed record ListSubjectsResponseDto(
    IReadOnlyList<SubjectRefDto> Subjects,
    string? ContinuationToken);

/// <summary>One node in the explain tree returned alongside an authorization decision.</summary>
public sealed record ExplainNodeDto(string Description, bool Allowed, IReadOnlyList<ExplainNodeDto> Children);
