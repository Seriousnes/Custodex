using System.Diagnostics;
using Relkit.Abstractions;
using Relkit.Core.Conditions;

namespace Relkit.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer : IAuthorizer, ICacheableAuthorizer
{
    private readonly ISchemaStore _schemaStore;
    private readonly IRelationStore _relations;
    private readonly IAttributeStore _attributes;
    private readonly IConditionEvaluator _conditions;
    private readonly EvaluationOptions _options;

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

    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var (allowed, _, explain) = await RunCheckAsync(request, ct);
        return new CheckResult(allowed, explain);
    }

    /// <summary>
    /// Internal entry point used by the m0/08 caching decorator: returns the decision
    /// together with whether any condition was reached, so the cache can avoid storing
    /// condition-dependent results. Never emits an Explain tree (caching path).
    /// </summary>
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
        using var activity = RelkitDiagnostics.ActivitySource.StartActivity("relkit.check");
        activity?.SetTag("relkit.object", request.Object.ToString());
        activity?.SetTag("relkit.permission", request.Permission);
        activity?.SetTag("relkit.subject", request.Subject.ToString());

        var start = Stopwatch.GetTimestamp();
        try
        {
            var index = await LoadSchemaAsync(request.Tenant.Store, ct);
            var ctx = new EvalContext(_options);
            var roots = request.Explain ? new List<ExplainNode>() : null;
            var allowed = await CheckPermissionAsync(
                index, request.Tenant, request.Object, request.Permission, request.Subject,
                request.Context, ctx, roots, ct);

            activity?.SetTag("relkit.allowed", allowed);
            activity?.SetTag("relkit.condition_touched", ctx.ConditionTouched);
            return (allowed, ctx.ConditionTouched, roots is { Count: > 0 } ? roots[0] : null);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            RelkitDiagnostics.CheckDuration.Record(elapsedMs);
        }
    }

    /// <summary>
    /// Pointwise membership: does <paramref name="subject"/> hold
    /// <paramref name="permission"/> on <paramref name="obj"/>? Evaluates the
    /// permission's <see cref="PermExpr"/> recursively. The optional
    /// <paramref name="explain"/> sink collects a trace node per visited branch.
    /// </summary>
    private async Task<bool> CheckPermissionAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, subject);
        if (ctx.TryGetMemo(frame, out var memoized) && explain is null)
            return memoized;

        if (!ctx.TryEnter(frame, out var scope))
            return false;   // cycle on the current path: contributes nothing

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

    /// <summary>Evaluates whether <paramref name="subject"/> fills <paramref name="obj"/>#<paramref name="relation"/>.</summary>
    private async Task<bool> ResolveRelationAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SubjectRef subject, RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (!ctx.TryEnterRelation(new EvalFrame(obj, relation, subject), out var scope))
            return false;   // relation cycle on the current path: contributes nothing
        using (scope)
        {
            var tuples = await _relations.GetByObjectAsync(tenant, obj, relation, ct);
            foreach (var tuple in tuples)
            {
                if (!await ConditionSatisfiedAsync(index, tenant, obj, tuple, context, ctx, ct))
                    continue;

                var s = tuple.Subject;

                // Wildcard: type:* grants every subject of that type.
                if (s.IsWildcard && string.Equals(s.Type, subject.Type, StringComparison.Ordinal))
                    return true;

                // Direct subject match.
                if (!s.IsSubjectSet && !s.IsWildcard
                    && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)
                    && string.Equals(s.Id, subject.Id, StringComparison.Ordinal))
                    return true;

                // Subject-set: group:G#rel — recurse into G's relation.
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

    /// <summary>
    /// Evaluates a tuple's carried condition. A tuple with no condition is always
    /// satisfied. Reaching a condition latches <see cref="EvalContext.ConditionTouched"/>
    /// so the m0/08 cache never caches this decision.
    /// </summary>
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
