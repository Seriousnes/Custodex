using Relkit.Abstractions;

namespace Relkit.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
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
            default:
                throw new NotImplementedException("Full algebra is implemented in Task 5.");
        }
    }
}
