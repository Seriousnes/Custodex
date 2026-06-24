using Npgsql;
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Postgres recursive-CTE primary path for the four authorization operations. Reachability
/// (nested subject-set expansion and arrow edges) runs in SQL via <see cref="CteReachability"/>;
/// the boolean algebra and conditions run in C# with the same semantics as the
/// <c>EngineDrivenAuthorizer</c>, so the two paths return identical answers.
/// </summary>
public sealed partial class NpgsqlCteAuthorizer : IAuthorizer
{
    private readonly string _connectionString;
    private readonly ISchemaStore _schemaStore;
    private readonly IAttributeStore _attributes;
    private readonly IConditionEvaluator _conditions;
    private readonly EvaluationOptions _options;

    /// <summary>
    /// Creates the CTE-backed authorizer. It opens a short-lived connection per
    /// <see cref="CheckAsync"/>/<see cref="BatchCheckAsync"/> from <paramref name="connectionString"/>
    /// and reads the active schema and attributes through the supplied stores.
    /// </summary>
    public NpgsqlCteAuthorizer(
        string connectionString, ISchemaStore schemaStore, IAttributeStore attributes,
        IConditionEvaluator conditions, EvaluationOptions? options = null)
    {
        _connectionString = connectionString;
        _schemaStore = schemaStore;
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

    /// <inheritdoc />
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var ctx = new EvalContext(_options);
        var explainSink = request.Explain ? new List<ExplainNode>() : null;
        var allowed = await CheckPermissionAsync(
            conn, index, request.Tenant, request.Object, request.Permission, request.Subject,
            request.Context, ctx, explainSink, ct);
        return new CheckResult(allowed, explainSink?.Count > 0 ? explainSink[0] : null);
    }

    /// <summary>Pointwise membership: does <paramref name="subject"/> hold <paramref name="permission"/> on <paramref name="obj"/>?</summary>
    private async Task<bool> CheckPermissionAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
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
            var result = await EvalExprAsync(conn, index, tenant, obj, def.Expression, subject, context, ctx, children, ct);
            explain?.Add(new ExplainNode($"{obj}#{permission}", result, children!));
            if (explain is null) ctx.SetMemo(frame, result);
            return result;
        }
    }

    /// <summary>
    /// Does <paramref name="subject"/> fill <paramref name="obj"/>#<paramref name="relation"/>?
    /// Direct / wildcard / nested subject-set. When no direct tuple on this relation carries a
    /// condition, the expanded-CTE fast path resolves nesting in SQL; otherwise direct edges are
    /// walked in C# so each tuple's condition is evaluated as the <c>EngineDrivenAuthorizer</c> does.
    /// The whole body is guarded against relation cycles on the current path.
    /// </summary>
    private async Task<bool> ResolveRelationAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SubjectRef subject, RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (!ctx.TryEnterRelation(new EvalFrame(obj, relation, subject), out var scope))
            return false;
        using (scope)
        {
            var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, relation, ct);
            var anyConditioned = edges.Any(e => e.Condition is not null);

            if (!anyConditioned)
            {
                var leaves = await CteReachability.SubjectsThroughRelationAsync(conn, null, tenant, obj, relation, ct);
                foreach (var leaf in leaves)
                {
                    if (leaf.IsWildcard && string.Equals(leaf.Type, subject.Type, StringComparison.Ordinal)) return true;
                    if (string.Equals(leaf.Type, subject.Type, StringComparison.Ordinal)
                        && string.Equals(leaf.Id, subject.Id, StringComparison.Ordinal)) return true;
                }
                return false;
            }

            foreach (var edge in edges)
            {
                if (!await ConditionSatisfiedAsync(index, tenant, obj, edge, context, ctx, ct)) continue;
                var s = edge.Subject;
                if (s.IsWildcard && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)) return true;
                if (!s.IsSubjectSet && !s.IsWildcard
                    && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)
                    && string.Equals(s.Id, subject.Id, StringComparison.Ordinal)) return true;
                if (s.IsSubjectSet
                    && await ResolveRelationAsync(conn, index, tenant, new EntityRef(s.Type, s.Id), s.Relation!, subject, context, ctx, ct))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Evaluates a tuple's carried condition. A tuple with no condition is satisfied. Latches <see cref="EvalContext.ConditionTouched"/>.</summary>
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
