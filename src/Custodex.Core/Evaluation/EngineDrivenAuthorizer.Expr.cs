using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    /// <summary>
    /// Pointwise evaluation of a permission sub-expression for a single subject.
    /// Returns true iff the subject is a member of the set the expression denotes
    /// on <paramref name="obj"/>. Boolean operators short-circuit; Arrow recurses
    /// into the related object's own permission expression (so inner exclusions and
    /// intersections are always honoured).
    /// </summary>
    private async Task<bool> EvalExprAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
            {
                var ok = await ResolveRelationAsync(index, tenant, obj, r.Relation, subject, context, ctx, ct);
                explain?.Add(new ExplainNode($"relation {r.Relation}", ok, Array.Empty<ExplainNode>()));
                return ok;
            }

            case Union u:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(index, tenant, obj, u.Left, subject, context, ctx, children, ct);
                if (left && explain is null) return true;   // short-circuit when not explaining
                // When explaining, always evaluate the right branch so the trace records both.
                var right = await EvalExprAsync(index, tenant, obj, u.Right, subject, context, ctx, children, ct);
                var result = left || right;
                explain?.Add(new ExplainNode("union (+)", result, children!));
                return result;
            }

            case Intersect i:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(index, tenant, obj, i.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) { return false; }   // short-circuit when not explaining
                var right = await EvalExprAsync(index, tenant, obj, i.Right, subject, context, ctx, children, ct);
                var result = left && right;
                explain?.Add(new ExplainNode("intersect (&)", result, children!));
                return result;
            }

            case Exclude e:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(index, tenant, obj, e.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) { return false; }   // short-circuit when not explaining
                var right = await EvalExprAsync(index, tenant, obj, e.Right, subject, context, ctx, children, ct);
                var result = left && !right;
                explain?.Add(new ExplainNode("exclude (-)", result, children!));
                return result;
            }

            case Arrow a:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var result = await EvalArrowAsync(index, tenant, obj, a, subject, context, ctx, children, ct);
                explain?.Add(new ExplainNode($"arrow {a.Relation}->{a.Permission}", result, children!));
                return result;
            }

            case Conditioned c:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var inner = await EvalExprAsync(index, tenant, obj, c.Inner, subject, context, ctx, children, ct);
                var passed = inner && await BranchConditionSatisfiedAsync(index, tenant, obj, c.ConditionName, context, ctx, ct);
                explain?.Add(new ExplainNode($"conditioned [{c.ConditionName}]", passed, children!));
                return passed;
            }

            default:
                throw new EvaluationLimitException($"Unhandled permission expression node '{expr.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Arrow: gather the objects related to <paramref name="obj"/> through
    /// <paramref name="arrow"/>.Relation and recurse into each one's
    /// <paramref name="arrow"/>.Permission. A related object's subject in the
    /// relation tuple is the target entity (e.g. <c>enclosure:KH1</c>). If the
    /// target type defines a permission of that name we evaluate the full
    /// permission; otherwise we fall back to a direct relation resolve.
    /// </summary>
    private async Task<bool> EvalArrowAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, Arrow arrow,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        var edges = await _relations.GetByObjectAsync(tenant, obj, arrow.Relation, ct);
        foreach (var edge in edges)
        {
            if (!await ConditionSatisfiedAsync(index, tenant, obj, edge, context, ctx, ct))
                continue;

            // The subject of a structural-reference tuple is the related entity.
            var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);

            bool hit;
            if (index.TryPermission(related.Type, arrow.Permission, out _))
                hit = await CheckPermissionAsync(index, tenant, related, arrow.Permission, subject, context, ctx, explain, ct);
            else
                hit = await ResolveRelationAsync(index, tenant, related, arrow.Permission, subject, context, ctx, ct);

            if (hit) return true;
        }
        return false;
    }

    /// <summary>
    /// Branch-level condition gate (a <see cref="Conditioned"/> node). Evaluated with
    /// empty parameters against request context + the object's synced attributes.
    /// Latches <see cref="EvalContext.ConditionTouched"/>.
    /// </summary>
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
