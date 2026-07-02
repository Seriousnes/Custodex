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

/// <summary>
/// The three-valued outcome of a Check. <see cref="Allow"/> and <see cref="Deny"/> are definite decisions;
/// <see cref="Conditional"/> means the structural relationship holds but a condition could not be resolved
/// because required, caller-suppliable context was absent — supplying it could still flip the answer either way.
/// </summary>
public enum CheckDecision
{
    /// <summary>The permission is denied. This is the default-deny outcome, including for unresolved definite failures.</summary>
    Deny = 0,

    /// <summary>The permission is granted.</summary>
    Allow = 1,

    /// <summary>
    /// The decision cannot be made definitely: a relationship reaches the subject, but a condition on that path
    /// could not be evaluated because required context keys were missing. The caveat and the missing keys are
    /// reported on <see cref="CheckResult.UnmetConditions"/>. A conditional decision is not a grant —
    /// <see cref="CheckResult.Allowed"/> is <see langword="false"/> — so default-deny is preserved for callers
    /// that cannot supply the missing context.
    /// </summary>
    Conditional = 2,
}

/// <summary>
/// A condition that stood between a <see cref="CheckRequest"/> and a definite decision: the engine reached a
/// relationship gated by this condition but could not evaluate it because context it reads was absent. Supplying
/// the named keys (through <see cref="RequestContext.Attributes"/> or the object's attributes) could change the outcome.
/// </summary>
/// <param name="Condition">The name of the schema condition that could not be resolved.</param>
/// <param name="MissingKeys">The attribute keys the condition read that were absent, in ordinal order.</param>
public sealed record UnmetCondition(string Condition, IReadOnlyList<string> MissingKeys);

/// <summary>The outcome of a Check: a three-valued decision, the conditions that blocked a definite answer, and an optional explain trace.</summary>
public sealed record CheckResult
{
    /// <summary>The three-valued decision: allow, deny, or conditional.</summary>
    public CheckDecision Decision { get; init; }

    /// <summary>Whether the permission is granted. <see langword="true"/> only when <see cref="Decision"/> is <see cref="CheckDecision.Allow"/>; a conditional decision reads as not granted.</summary>
    public bool Allowed => Decision == CheckDecision.Allow;

    /// <summary>
    /// The conditions that prevented a definite decision, each naming the condition and the missing context keys.
    /// Empty for <see cref="CheckDecision.Allow"/> and <see cref="CheckDecision.Deny"/>; deterministically ordered
    /// and de-duplicated for <see cref="CheckDecision.Conditional"/>.
    /// </summary>
    public IReadOnlyList<UnmetCondition> UnmetConditions { get; init; }

    /// <summary>The root of the explain trace when one was requested; otherwise <see langword="null"/>.</summary>
    public ExplainNode? Explain { get; init; }

    /// <summary>Creates a result from a three-valued decision.</summary>
    /// <param name="decision">The decision reached.</param>
    /// <param name="unmetConditions">The conditions that blocked a definite answer; <see langword="null"/> is treated as none.</param>
    /// <param name="explain">The explain trace when one was requested; otherwise <see langword="null"/>.</param>
    public CheckResult(CheckDecision decision, IReadOnlyList<UnmetCondition>? unmetConditions = null, ExplainNode? explain = null)
    {
        Decision = decision;
        UnmetConditions = unmetConditions ?? [];
        Explain = explain;
    }

    /// <summary>Creates a definite allow or deny result. A shorthand for the two-valued outcome.</summary>
    /// <param name="allowed"><see langword="true"/> for <see cref="CheckDecision.Allow"/>; otherwise <see cref="CheckDecision.Deny"/>.</param>
    /// <param name="explain">The explain trace when one was requested; otherwise <see langword="null"/>.</param>
    public CheckResult(bool allowed, ExplainNode? explain = null)
        : this(allowed ? CheckDecision.Allow : CheckDecision.Deny, null, explain) { }
}

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
