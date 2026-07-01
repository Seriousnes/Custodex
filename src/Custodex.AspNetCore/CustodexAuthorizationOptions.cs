using System.Security.Claims;

using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>
/// Configures how the ASP.NET Core adapter maps a request to a Custodex check: the policy-name
/// shape, the subject and tenant claim names, the fallback object for object-less policies, and
/// how engine errors are surfaced.
/// </summary>
public sealed class CustodexAuthorizationOptions
{
    /// <summary>The leading segment that marks a policy name as a Custodex policy. Defaults to <c>custodex</c>.</summary>
    public string PolicyPrefix { get; set; } = "custodex";

    /// <summary>The separator between policy-name segments. Defaults to <c>:</c>.</summary>
    public string PolicySeparator { get; set; } = ":";

    /// <summary>
    /// The segment that marks the any-object (exists) form <c>{prefix}:{any}:{type}:{permission}</c>,
    /// which authorizes when the subject holds the permission on at least one object of the type.
    /// Defaults to <c>any</c>.
    /// </summary>
    public string AnyObjectSegment { get; set; } = "any";

    /// <summary>The subject entity type used for every resolved subject. Defaults to <c>user</c>.</summary>
    public string SubjectType { get; set; } = "user";

    /// <summary>The claim whose value is the subject id. Defaults to <see cref="ClaimTypes.NameIdentifier"/>.</summary>
    public string SubjectIdClaim { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>The claim whose value is the store id. Defaults to <see cref="CustodexClaimTypes.Store"/>.</summary>
    public string StoreClaim { get; set; } = CustodexClaimTypes.Store;

    /// <summary>The claim whose value is the tenant id when no tenant header is present. Defaults to <see cref="CustodexClaimTypes.Tenant"/>.</summary>
    public string TenantClaim { get; set; } = CustodexClaimTypes.Tenant;

    /// <summary>The header whose value is the tenant id. Defaults to <see cref="CustodexHeaders.Tenant"/>.</summary>
    public string TenantHeader { get; set; } = CustodexHeaders.Tenant;

    /// <summary>
    /// Produces the object to evaluate when no specific object can be resolved from the resource or
    /// route (for example a page-level policy). Returns <see langword="null"/> to deny. Defaults to
    /// <see langword="null"/>.
    /// </summary>
    public Func<CustodexResolutionContext, EntityRef?>? RootObject { get; set; }

    /// <summary>
    /// When <see langword="true"/>, an exception from the engine is rethrown; when
    /// <see langword="false"/> (the default) it is logged and treated as a denial.
    /// </summary>
    public bool ThrowOnEvaluationError { get; set; }

    /// <summary>
    /// Tunes the DI-scoped in-process decision cache the handler consults around each engine check.
    /// </summary>
    public DecisionCacheOptions DecisionCache { get; } = new();
}
