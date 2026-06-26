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
public sealed partial class EngineDrivenAuthorizer : IAuthorizer, ICacheableAuthorizer
{
    private readonly ISchemaStore _schemaStore;
    private readonly IRelationStore _relations;
    private readonly IAttributeStore _attributes;
    private readonly IConditionEvaluator _conditions;
    private readonly EvaluationOptions _options;

    /// <summary>Creates an authorizer over the given stores and condition evaluator.</summary>
    /// <param name="schemaStore">Supplies the active schema for the request's store.</param>
    /// <param name="relations">The relation tuple store queried during evaluation.</param>
    /// <param name="attributes">The attribute store read when evaluating conditions.</param>
    /// <param name="conditions">Evaluates the predicate carried by a conditioned tuple.</param>
    /// <param name="options">Evaluation limits such as the maximum recursion depth; defaults are used when omitted.</param>
    public EngineDrivenAuthorizer(
        ISchemaStore schemaStore,
        IRelationStore relations,
        IAttributeStore attributes,
        IConditionEvaluator conditions,
        EvaluationOptions? options = null)
    {
        _schemaStore = schemaStore;
        _relations = relations;
        _attributes = attributes;
        _conditions = conditions;
        _options = options ?? new EvaluationOptions();
    }

    private async Task<SchemaIndex> LoadSchemaAsync(string store, CancellationToken ct)
    {
        var schema = await _schemaStore.GetActiveAsync(store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{store}'>");
        return new SchemaIndex(schema);
    }

    /// <inheritdoc/>
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var (allowed, _, explain) = await RunCheckAsync(request, ct);
        return new CheckResult(allowed, explain);
    }

    internal async Task<(bool Allowed, bool ConditionTouched)> CheckInternalAsync(
        CheckRequest request, CancellationToken ct = default)
    {
        var (allowed, conditionTouched, _) = await RunCheckAsync(
            request with { Explain = false }, ct);
        return (allowed, conditionTouched);
    }

    Task<(bool Allowed, bool ConditionTouched)> ICacheableAuthorizer.CheckInternalAsync(
        CheckRequest request, CancellationToken ct) => CheckInternalAsync(request, ct);

    private async Task<(bool Allowed, bool ConditionTouched, ExplainNode? Explain)> RunCheckAsync(
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
            var allowed = await CheckPermissionAsync(
                index, request.Tenant, request.Object, request.Permission, request.Subject,
                request.Context, ctx, roots, ct);

            activity?.SetTag("Custodex.allowed", allowed);
            activity?.SetTag("Custodex.condition_touched", ctx.ConditionTouched);
            return (allowed, ctx.ConditionTouched, roots is { Count: > 0 } ? roots[0] : null);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            CustodexDiagnostics.CheckDuration.Record(elapsedMs);
        }
    }

    private async Task<bool> CheckPermissionAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, subject);
        if (ctx.TryGetMemo(frame, out var memoized) && explain is null)
            return memoized;

        if (!ctx.TryEnter(frame, out var scope))
            return false;

        using (scope)
        {
            var def = index.Permission(obj.Type, permission);
            var children = explain is null ? null : new List<ExplainNode>();
            var result = await EvalExprAsync(index, tenant, obj, def.Expression, subject, context, ctx, children, ct);
            explain?.Add(new ExplainNode($"{obj}#{permission}", result, children!));
            if (explain is null) ctx.SetMemo(frame, result);
            return result;
        }
    }

    private async Task<bool> ResolveRelationAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SubjectRef subject, RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (!ctx.TryEnterRelation(new EvalFrame(obj, relation, subject), out var scope))
            return false;
        using (scope)
        {
            var tuples = await _relations.GetByObjectAsync(tenant, obj, relation, ct);
            foreach (var tuple in tuples)
            {
                if (!await ConditionSatisfiedAsync(index, tenant, obj, tuple, context, ctx, ct))
                    continue;

                var s = tuple.Subject;

                if (s.IsWildcard && string.Equals(s.Type, subject.Type, StringComparison.Ordinal))
                    return true;

                if (!s.IsSubjectSet && !s.IsWildcard
                    && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)
                    && string.Equals(s.Id, subject.Id, StringComparison.Ordinal))
                    return true;

                if (s.IsSubjectSet)
                {
                    var nestedObj = new EntityRef(s.Type, s.Id);
                    if (await ResolveRelationAsync(index, tenant, nestedObj, s.Relation!, subject, context, ctx, ct))
                        return true;
                }
            }
            return false;
        }
    }

    private async Task<bool> ConditionSatisfiedAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, RelationTuple tuple,
        RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (tuple.Condition is null) return true;
        ctx.MarkConditionTouched();
        var def = index.Condition(tuple.Condition.Name);
        var attrs = await _attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        return _conditions.Evaluate(def, tuple.Condition, attrs, context);
    }
}
