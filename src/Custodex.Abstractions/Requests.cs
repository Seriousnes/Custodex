namespace Custodex.Abstractions;

/// <summary>A point-in-time Check: does <paramref name="Subject"/> hold <paramref name="Permission"/> on <paramref name="Object"/>?</summary>
/// <param name="Tenant">The isolation scope to evaluate within.</param>
/// <param name="Object">The object whose permission is tested.</param>
/// <param name="Permission">The permission (or relation) name to evaluate.</param>
/// <param name="Subject">The subject the decision is about.</param>
/// <param name="Context">The request context (time and attributes) for condition evaluation.</param>
/// <param name="Explain">When <see langword="true"/>, the result carries a trace of how the decision was reached.</param>
public sealed record CheckRequest(
    TenantContext Tenant, EntityRef Object, string Permission, SubjectRef Subject,
    RequestContext Context, bool Explain = false);

/// <summary>One node in an explain trace, describing a sub-decision and its contribution to the outcome.</summary>
/// <param name="Description">A human-readable account of the expression evaluated at this node.</param>
/// <param name="Allowed">Whether this node evaluated to allow.</param>
/// <param name="Children">The nested sub-decisions that produced this node's result.</param>
public sealed record ExplainNode(string Description, bool Allowed, IReadOnlyList<ExplainNode> Children);

/// <summary>The outcome of a Check: the allow/deny decision, with an optional explain trace.</summary>
/// <param name="Allowed">Whether the permission is granted.</param>
/// <param name="Explain">The root of the explain trace when one was requested; otherwise <see langword="null"/>.</param>
public sealed record CheckResult(bool Allowed, ExplainNode? Explain = null);

/// <summary>One object/permission/subject triple within a <see cref="BatchCheckRequest"/>.</summary>
/// <param name="Object">The object whose permission is tested.</param>
/// <param name="Permission">The permission (or relation) name to evaluate.</param>
/// <param name="Subject">The subject the decision is about.</param>
public sealed record CheckItem(EntityRef Object, string Permission, SubjectRef Subject);

/// <summary>Several Checks evaluated together under one tenant and request context.</summary>
/// <param name="Tenant">The isolation scope shared by every item.</param>
/// <param name="Items">The triples to decide; each is answered independently.</param>
/// <param name="Context">The request context shared by every item.</param>
public sealed record BatchCheckRequest(TenantContext Tenant, IReadOnlyList<CheckItem> Items, RequestContext Context);

/// <summary>
/// A forward enumeration: the objects of <paramref name="ObjectType"/> on which <paramref name="Subject"/>
/// holds <paramref name="Permission"/>. Results are paged in ordinal id order.
/// </summary>
/// <param name="Tenant">The isolation scope to enumerate within.</param>
/// <param name="Subject">The subject whose accessible objects are listed.</param>
/// <param name="ObjectType">The entity type to enumerate.</param>
/// <param name="Permission">The permission the subject must hold on each returned object.</param>
/// <param name="Context">The request context (time and attributes) for condition evaluation.</param>
/// <param name="PageSize">The maximum number of ids to return in this page.</param>
/// <param name="ContinuationToken">A cursor from a prior page, or <see langword="null"/> to start from the beginning.</param>
public sealed record ListObjectsRequest(
    TenantContext Tenant, SubjectRef Subject, string ObjectType, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);

/// <summary>A page of <see cref="ListObjectsRequest"/> results.</summary>
/// <param name="ObjectIds">The matching object ids in this page, in ordinal order.</param>
/// <param name="ContinuationToken">The cursor to pass for the next page, or <see langword="null"/> when the enumeration is exhausted.</param>
public sealed record ListObjectsResult(IReadOnlyList<string> ObjectIds, string? ContinuationToken);

/// <summary>
/// A reverse enumeration: the subjects that hold <paramref name="Permission"/> on <paramref name="Object"/>.
/// Results are paged.
/// </summary>
/// <param name="Tenant">The isolation scope to enumerate within.</param>
/// <param name="Object">The object whose authorized subjects are listed.</param>
/// <param name="Permission">The permission each returned subject must hold.</param>
/// <param name="Context">The request context (time and attributes) for condition evaluation.</param>
/// <param name="PageSize">The maximum number of subjects to return in this page.</param>
/// <param name="ContinuationToken">A cursor from a prior page, or <see langword="null"/> to start from the beginning.</param>
public sealed record ListSubjectsRequest(
    TenantContext Tenant, EntityRef Object, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);

/// <summary>A page of <see cref="ListSubjectsRequest"/> results.</summary>
/// <param name="Subjects">The matching subjects in this page.</param>
/// <param name="ContinuationToken">The cursor to pass for the next page, or <see langword="null"/> when the enumeration is exhausted.</param>
public sealed record ListSubjectsResult(IReadOnlyList<SubjectRef> Subjects, string? ContinuationToken);
