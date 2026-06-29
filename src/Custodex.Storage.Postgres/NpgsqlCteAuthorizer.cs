using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;

using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Postgres recursive-CTE primary path for the four authorization operations. Reachability
/// (nested subject-set expansion and arrow edges) runs in SQL via <see cref="CteReachability"/>;
/// the boolean algebra and conditions run in C# with the same semantics as the
/// <c>EngineDrivenAuthorizer</c>, so the two paths return identical answers.
/// </summary>
/// <remarks>
/// Creates the CTE-backed authorizer. It opens a short-lived connection per
/// <see cref="CheckAsync"/>/<see cref="BatchCheckAsync"/> from <paramref name="connectionString"/>
/// and reads the active schema and attributes through the supplied stores.
/// </remarks>
public sealed partial class NpgsqlCteAuthorizer(
    string connectionString, ISchemaStore schemaStore, IAttributeStore attributes,
    IConditionEvaluator conditions, EvaluationOptions? options = null) : IAuthorizer
{
    private readonly string _connectionString = CustodexSchema.Apply(connectionString);
    private readonly ISchemaStore _schemaStore = schemaStore;
    private readonly IAttributeStore _attributes = attributes;
    private readonly IConditionEvaluator _conditions = conditions;
    private readonly EvaluationOptions _options = options ?? new EvaluationOptions();
    private readonly NpgsqlUnitOfWork? _bound;

    private NpgsqlCteAuthorizer(
        string connectionString, ISchemaStore schemaStore, IAttributeStore attributes,
        IConditionEvaluator conditions, EvaluationOptions options, NpgsqlUnitOfWork bound)
        : this(connectionString, schemaStore, attributes, conditions, options) => _bound = bound;

    /// <summary>
    /// Returns an authorizer whose reads run on the connection and transaction carried by
    /// <paramref name="uow"/>, so a check or enumeration observes tuples written earlier on that same
    /// uncommitted unit of work. The returned authorizer borrows the connection and never disposes it;
    /// the original authorizer is unchanged and keeps opening its own short-lived connection per call.
    /// </summary>
    /// <param name="uow">The unit of work whose connection and transaction the reads run on.</param>
    /// <returns>An authorizer bound to the supplied unit of work.</returns>
    public NpgsqlCteAuthorizer OnUnitOfWork(IUnitOfWork uow) =>
        new(_connectionString, _schemaStore, _attributes, _conditions, _options, NpgsqlUnitOfWork.From(uow));

    private NpgsqlTransaction? BoundTx => _bound?.Transaction;

    private async Task<T> RunAsync<T>(Func<NpgsqlConnection, Task<T>> body, CancellationToken ct)
    {
        if (_bound is { } b)
            return await body(b.Connection);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await body(conn);
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
        return await RunAsync(async conn =>
        {
            var ctx = new EvalContext(_options);
            var explainSink = request.Explain ? new List<ExplainNode>() : null;
            var allowed = await CheckPermissionAsync(
                conn, index, request.Tenant, request.Object, request.Permission, request.Subject,
                request.Context, ctx, explainSink, ct);
            return new CheckResult(allowed, explainSink?.Count > 0 ? explainSink[0] : null);
        }, ct);
    }

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

    private async Task<bool> ResolveRelationAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SubjectRef subject, RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (!ctx.TryEnterRelation(new EvalFrame(obj, relation, subject), out var scope))
            return false;
        using (scope)
        {
            var edges = await CteReachability.EdgesThroughRelationAsync(conn, BoundTx, tenant, obj, relation, ct);
            var conditionsMayApply = index.HasConditions || edges.Any(e => e.Condition is not null);

            if (!conditionsMayApply)
            {
                var leaves = await CteReachability.SubjectsThroughRelationAsync(conn, BoundTx, tenant, obj, relation, ct);
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
