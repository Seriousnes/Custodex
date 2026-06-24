using Npgsql;
using Custodex.Abstractions;
using Custodex.Core.Evaluation;

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
                var ok = await ResolveRelationAsync(conn, index, tenant, obj, r.Relation, subject, context, ctx, ct);
                explain?.Add(new ExplainNode($"relation {r.Relation}", ok, Array.Empty<ExplainNode>()));
                return ok;
            }
            default:
                throw new NotImplementedException("Full algebra is implemented in a later step.");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();
    /// <inheritdoc />
    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();
    /// <inheritdoc />
    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();
}
