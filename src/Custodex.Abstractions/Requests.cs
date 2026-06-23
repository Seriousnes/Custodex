namespace Custodex.Abstractions;

public sealed record CheckRequest(
    TenantContext Tenant, EntityRef Object, string Permission, SubjectRef Subject,
    RequestContext Context, bool Explain = false);

public sealed record ExplainNode(string Description, bool Allowed, IReadOnlyList<ExplainNode> Children);
public sealed record CheckResult(bool Allowed, ExplainNode? Explain = null);

public sealed record CheckItem(EntityRef Object, string Permission, SubjectRef Subject);
public sealed record BatchCheckRequest(TenantContext Tenant, IReadOnlyList<CheckItem> Items, RequestContext Context);

public sealed record ListObjectsRequest(
    TenantContext Tenant, SubjectRef Subject, string ObjectType, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListObjectsResult(IReadOnlyList<string> ObjectIds, string? ContinuationToken);

public sealed record ListSubjectsRequest(
    TenantContext Tenant, EntityRef Object, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListSubjectsResult(IReadOnlyList<SubjectRef> Subjects, string? ContinuationToken);
