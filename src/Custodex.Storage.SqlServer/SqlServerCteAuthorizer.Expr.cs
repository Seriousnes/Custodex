using Custodex.Abstractions;
using Custodex.Core.Evaluation;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

public sealed partial class SqlServerCteAuthorizer
{
    private async Task<EvalOutcome> EvalExprAsync(
        SqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
            {
                if (index.TryRelation(obj.Type, r.Relation, out _))
                {
                    var ok = await ResolveRelationAsync(conn, index, tenant, obj, r.Relation, subject, context, ctx, ct);
                    explain?.Add(new ExplainNode($"relation {r.Relation}", ok.IsTrue, []));
                    return ok;
                }

                return await CheckPermissionAsync(conn, index, tenant, obj, r.Relation, subject, context, ctx, explain, ct);
            }

            case Union u:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, u.Left, subject, context, ctx, children, ct);
                if (left.IsTrue && explain is null) return EvalOutcome.True;
                var right = await EvalExprAsync(conn, index, tenant, obj, u.Right, subject, context, ctx, children, ct);
                var result = EvalOutcome.Or(left, right);
                explain?.Add(new ExplainNode("union (+)", result.IsTrue, children!));
                return result;
            }

            case Intersect i:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, i.Left, subject, context, ctx, children, ct);
                if (left.Truth == EvalTruth.False && explain is null) return EvalOutcome.False;
                var right = await EvalExprAsync(conn, index, tenant, obj, i.Right, subject, context, ctx, children, ct);
                var result = EvalOutcome.And(left, right);
                explain?.Add(new ExplainNode("intersect (&)", result.IsTrue, children!));
                return result;
            }

            case Exclude e:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, e.Left, subject, context, ctx, children, ct);
                if (left.Truth == EvalTruth.False && explain is null) return EvalOutcome.False;
                EvalOutcome right;
                using (ctx.EnterNegation())
                    right = await EvalExprAsync(conn, index, tenant, obj, e.Right, subject, context, ctx, children, ct);
                var result = EvalOutcome.Exclude(left, right);
                explain?.Add(new ExplainNode("exclude (-)", result.IsTrue, children!));
                return result;
            }

            case Arrow a:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var result = await EvalArrowAsync(conn, index, tenant, obj, a, subject, context, ctx, children, ct);
                explain?.Add(new ExplainNode($"arrow {a.Relation}->{a.Permission}", result.IsTrue, children!));
                return result;
            }

            case Conditioned c:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var inner = await EvalExprAsync(conn, index, tenant, obj, c.Inner, subject, context, ctx, children, ct);
                EvalOutcome result;
                if (inner.Truth == EvalTruth.False)
                    result = EvalOutcome.False;
                else
                {
                    var cond = await BranchConditionOutcomeAsync(index, tenant, obj, c.ConditionName, context, ctx, ct);
                    result = EvalOutcome.And(inner, cond);
                }
                explain?.Add(new ExplainNode($"conditioned [{c.ConditionName}]", result.IsTrue, children!));
                return result;
            }

            default:
                throw new EvaluationLimitException($"Unhandled permission expression node '{expr.GetType().Name}'.");
        }
    }

    private async Task<EvalOutcome> EvalArrowAsync(
        SqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, Arrow arrow,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        var edges = await SqlServerReachability.EdgesThroughRelationAsync(conn, BoundTx, tenant, obj, arrow.Relation, ct);
        var acc = EvalOutcome.False;
        foreach (var edge in edges)
        {
            var cond = await ConditionOutcomeAsync(index, tenant, obj, edge, context, ctx, ct);
            if (cond.Truth == EvalTruth.False) continue;
            var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
            EvalOutcome hit = index.TryPermission(related.Type, arrow.Permission, out _)
                ? await CheckPermissionAsync(conn, index, tenant, related, arrow.Permission, subject, context, ctx, explain, ct)
                : await ResolveRelationAsync(conn, index, tenant, related, arrow.Permission, subject, context, ctx, ct);
            var contribution = EvalOutcome.And(cond, hit);
            if (contribution.IsTrue) return EvalOutcome.True;
            if (contribution.IsUnknown) acc = EvalOutcome.Or(acc, contribution);
        }
        return acc;
    }

    private async Task<EvalOutcome> BranchConditionOutcomeAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string conditionName,
        RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        ctx.MarkConditionTouched();
        var def = index.Condition(conditionName);
        var invocation = new ConditionRef(conditionName, new Dictionary<string, object?>());
        var attrs = await _attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        var result = _conditions.Evaluate(def, invocation, attrs, context);
        return EvalOutcome.FromCondition(conditionName, result);
    }
}
