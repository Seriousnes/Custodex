using Custodex.Abstractions;
using Custodex.Core.Evaluation;

using Npgsql;

namespace Custodex.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    private async Task<bool> EvalExprAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
            {
                if (index.TryRelation(obj.Type, r.Relation, out _))
                {
                    var ok = await ResolveRelationAsync(conn, index, tenant, obj, r.Relation, subject, context, ctx, ct);
                    explain?.Add(new ExplainNode($"relation {r.Relation}", ok, []));
                    return ok;
                }

                return await CheckPermissionAsync(conn, index, tenant, obj, r.Relation, subject, context, ctx, explain, ct);
            }

            case Union u:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, u.Left, subject, context, ctx, children, ct);
                if (left && explain is null) return true;
                var right = await EvalExprAsync(conn, index, tenant, obj, u.Right, subject, context, ctx, children, ct);
                var result = left || right;
                explain?.Add(new ExplainNode("union (+)", result, children!));
                return result;
            }

            case Intersect i:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, i.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) return false;
                var right = await EvalExprAsync(conn, index, tenant, obj, i.Right, subject, context, ctx, children, ct);
                var result = left && right;
                explain?.Add(new ExplainNode("intersect (&)", result, children!));
                return result;
            }

            case Exclude e:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, e.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) return false;
                bool right;
                using (ctx.EnterNegation())
                    right = await EvalExprAsync(conn, index, tenant, obj, e.Right, subject, context, ctx, children, ct);
                var result = left && !right;
                explain?.Add(new ExplainNode("exclude (-)", result, children!));
                return result;
            }

            case Arrow a:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var result = await EvalArrowAsync(conn, index, tenant, obj, a, subject, context, ctx, children, ct);
                explain?.Add(new ExplainNode($"arrow {a.Relation}->{a.Permission}", result, children!));
                return result;
            }

            case Conditioned c:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var inner = await EvalExprAsync(conn, index, tenant, obj, c.Inner, subject, context, ctx, children, ct);
                var passed = inner && await BranchConditionSatisfiedAsync(index, tenant, obj, c.ConditionName, context, ctx, ct);
                explain?.Add(new ExplainNode($"conditioned [{c.ConditionName}]", passed, children!));
                return passed;
            }

            default:
                throw new EvaluationLimitException($"Unhandled permission expression node '{expr.GetType().Name}'.");
        }
    }

    private async Task<bool> EvalArrowAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, Arrow arrow,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        var edges = await CteReachability.EdgesThroughRelationAsync(conn, BoundTx, tenant, obj, arrow.Relation, ct);
        foreach (var edge in edges)
        {
            if (!await ConditionSatisfiedAsync(index, tenant, obj, edge, context, ctx, ct)) continue;
            var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
            bool hit = index.TryPermission(related.Type, arrow.Permission, out _)
                ? await CheckPermissionAsync(conn, index, tenant, related, arrow.Permission, subject, context, ctx, explain, ct)
                : await ResolveRelationAsync(conn, index, tenant, related, arrow.Permission, subject, context, ctx, ct);
            if (hit) return true;
        }
        return false;
    }

    private async Task<bool> BranchConditionSatisfiedAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string conditionName,
        RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        ctx.MarkConditionTouched();
        var def = index.Condition(conditionName);
        var invocation = new ConditionRef(conditionName, new Dictionary<string, object?>());
        var attrs = await _attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        return _conditions.Evaluate(def, invocation, attrs, context);
    }

}
