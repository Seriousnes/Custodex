using System.Diagnostics;

using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core.Evaluation;

/// <summary>
/// Portable, engine-driven <see cref="IAuthorizer"/>: a C# walk over each permission's
/// expression that issues batched indexed lookups through the relation and attribute stores.
/// It runs the same decision semantics on any storage provider and serves as the reference
/// implementation other authorizers are validated against.
/// </summary>
/// <remarks>Creates an authorizer over the given stores and condition evaluator.</remarks>
/// <param name="schemaStore">Supplies the active schema for the request's store.</param>
/// <param name="relations">The relation tuple store queried during evaluation.</param>
/// <param name="attributes">The attribute store read when evaluating conditions.</param>
/// <param name="conditions">Evaluates the predicate carried by a conditioned tuple.</param>
/// <param name="options">Evaluation limits such as the maximum recursion depth; defaults are used when omitted.</param>
public sealed partial class EngineDrivenAuthorizer(
    ISchemaStore schemaStore,
    IRelationStore relations,
    IAttributeStore attributes,
    IConditionEvaluator conditions,
    EvaluationOptions? options = null) : IAuthorizer, ICacheableAuthorizer
{
    private readonly ISchemaStore _schemaStore = schemaStore;
    private readonly IRelationStore _relations = relations;
    private readonly IAttributeStore _attributes = attributes;
    private readonly IConditionEvaluator _conditions = conditions;
    private readonly EvaluationOptions _options = options ?? new EvaluationOptions();

    private async Task<SchemaIndex> LoadSchemaAsync(string store, CancellationToken ct)
    {
        var schema = await _schemaStore.GetActiveAsync(store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{store}'>");
        return new SchemaIndex(schema);
    }

    /// <inheritdoc/>
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var (outcome, _, explain) = await RunCheckAsync(request, ct);
        return outcome.ToCheckResult(explain);
    }

    internal async Task<(CheckResult Result, bool ConditionTouched)> CheckInternalAsync(
        CheckRequest request, CancellationToken ct = default)
    {
        var (outcome, conditionTouched, _) = await RunCheckAsync(
            request with { Explain = false }, ct);
        return (outcome.ToCheckResult(), conditionTouched);
    }

    Task<(CheckResult Result, bool ConditionTouched)> ICacheableAuthorizer.CheckInternalAsync(
        CheckRequest request, CancellationToken ct) => CheckInternalAsync(request, ct);

    private async Task<(EvalOutcome Outcome, bool ConditionTouched, ExplainNode? Explain)> RunCheckAsync(
        CheckRequest request, CancellationToken ct)
    {
        using var activity = CustodexDiagnostics.ActivitySource.StartActivity("Custodex.check");
        activity?.SetTag("Custodex.object", request.Object.ToString());
        activity?.SetTag("Custodex.permission", request.Permission);
        activity?.SetTag("Custodex.subject", request.Subject.ToString());

        var start = Stopwatch.GetTimestamp();
        try
        {
            var index = await LoadSchemaAsync(request.Tenant.Store, ct);
            var ctx = new EvalContext(_options);
            var roots = request.Explain ? new List<ExplainNode>() : null;
            var outcome = await CheckPermissionAsync(
                index, request.Tenant, request.Object, request.Permission, request.Subject,
                request.Context, ctx, roots, ct);

            activity?.SetTag("Custodex.decision", outcome.Truth.ToString());
            activity?.SetTag("Custodex.condition_touched", ctx.ConditionTouched);
            return (outcome, ctx.ConditionTouched, roots is { Count: > 0 } ? roots[0] : null);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            CustodexDiagnostics.CheckDuration.Record(elapsedMs);
        }
    }

    private async Task<EvalOutcome> CheckPermissionAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, subject);
        if (ctx.TryGetMemo(frame, out var memoized) && explain is null)
            return memoized;

        if (!ctx.TryEnter(frame, out var scope))
            return EvalOutcome.False;

        using (scope)
        {
            var def = index.Permission(obj.Type, permission);
            var children = explain is null ? null : new List<ExplainNode>();
            var result = await EvalExprAsync(index, tenant, obj, def.Expression, subject, context, ctx, children, ct);
            explain?.Add(new ExplainNode($"{obj}#{permission}", result.IsTrue, children!));
            if (explain is null) ctx.SetMemo(frame, result);
            return result;
        }
    }

    private async Task<EvalOutcome> ResolveRelationAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SubjectRef subject, RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (!ctx.TryEnterRelation(new EvalFrame(obj, relation, subject), out var scope))
            return EvalOutcome.False;
        using (scope)
        {
            var tuples = await _relations.GetByObjectAsync(tenant, obj, relation, ct);
            var acc = EvalOutcome.False;
            foreach (var tuple in tuples)
            {
                var cond = await ConditionOutcomeAsync(index, tenant, obj, tuple, context, ctx, ct);
                if (cond.Truth == EvalTruth.False) continue;

                var structural = await MatchTupleAsync(index, tenant, tuple, subject, context, ctx, ct);
                var contribution = EvalOutcome.And(cond, structural);
                if (contribution.IsTrue) return EvalOutcome.True;
                if (contribution.IsUnknown) acc = EvalOutcome.Or(acc, contribution);
            }
            return acc;
        }
    }

    private async Task<EvalOutcome> MatchTupleAsync(
        SchemaIndex index, TenantContext tenant, RelationTuple tuple, SubjectRef subject,
        RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        var s = tuple.Subject;

        if (s.IsWildcard && string.Equals(s.Type, subject.Type, StringComparison.Ordinal))
            return EvalOutcome.True;

        if (!s.IsSubjectSet && !s.IsWildcard
            && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)
            && string.Equals(s.Id, subject.Id, StringComparison.Ordinal))
            return EvalOutcome.True;

        if (s.IsSubjectSet)
            return await ResolveRelationAsync(index, tenant, new EntityRef(s.Type, s.Id), s.Relation!, subject, context, ctx, ct);

        return EvalOutcome.False;
    }

    private async Task<EvalOutcome> ConditionOutcomeAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, RelationTuple tuple,
        RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (tuple.Condition is null) return EvalOutcome.True;
        ctx.MarkConditionTouched();
        var def = index.Condition(tuple.Condition.Name);
        var attrs = await _attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        var result = _conditions.Evaluate(def, tuple.Condition, attrs, context);
        return EvalOutcome.FromCondition(tuple.Condition.Name, result);
    }
}
