using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

/// <summary>One probed grant: whether it holds structurally, and whether any condition was reached.</summary>
public sealed record StructuralGrant(bool Granted, bool Conditioned);

public sealed partial class EngineDrivenAuthorizer
{
    internal Task<StructuralGrant> CheckStructuralForTest(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission, SubjectRef subject,
        RequestContext context, CancellationToken ct = default)
        => CheckStructuralAsync(index, tenant, obj, permission, subject, context, ct);

    /// <summary>
    /// Probes whether <paramref name="subject"/> holds <paramref name="permission"/> on
    /// <paramref name="obj"/> structurally, reporting whether the grant path touched a condition.
    /// Intended to be called on an authorizer constructed with <c>NullConditionEvaluator</c> so
    /// conditions are treated as satisfied and the result is the optimistic structural grant; the
    /// latched <see cref="EvalContext.ConditionTouched"/> becomes the row's <c>conditioned</c> flag.
    /// The walk runs in a marking mode that, for schemas carrying conditions, refuses to drop an
    /// exclusion whose right could conditionally fail to hold: such a grant is kept and flagged
    /// conditioned so it is re-checked under real conditions at query time rather than over-denied.
    /// Public because the reverse-index rebuild calls it across assembly boundaries.
    /// </summary>
    public async Task<StructuralGrant> CheckStructuralAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission, SubjectRef subject,
        RequestContext context, CancellationToken ct)
    {
        var ctx = new EvalContext(_options) { StructuralMarking = true };
        var granted = await CheckPermissionAsync(index, tenant, obj, permission, subject, context, ctx, explain: null, ct);
        return new StructuralGrant(granted, granted && ctx.ConditionTouched);
    }
}
