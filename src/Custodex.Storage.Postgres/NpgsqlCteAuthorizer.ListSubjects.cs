using Custodex.Abstractions;
using Custodex.Core.Evaluation;

using Npgsql;

namespace Custodex.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    private static readonly string Nul = ((char)0).ToString();

    private static string SubjectKey(SubjectRef s) => s.Type + Nul + s.Id;

    private static readonly IComparer<SubjectRef> SubjectOrder =
        Comparer<SubjectRef>.Create((a, b) => string.CompareOrdinal(SubjectKey(a), SubjectKey(b)));

    /// <inheritdoc />
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.Object.Type, request.Permission);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var candidates = new SortedSet<SubjectRef>(SubjectOrder);
        var visited = new HashSet<EvalFrame>();
        await CollectLeafSubjectsAsync(conn, index, request.Tenant, request.Object, request.Permission,
            candidates, visited, ct);

        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<SubjectRef>(request.PageSize);
        string? lastKey = null;
        var exhausted = true;

        foreach (var subject in candidates)
        {
            var key = SubjectKey(subject);
            if (after is not null && string.CompareOrdinal(key, after) <= 0) continue;

            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, request.Object, request.Permission, subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(subject);
            lastKey = key;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = !await AnySubjectConfirmedAfterAsync(conn, index, request, candidates, key, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastKey!);
        return new ListSubjectsResult(confirmed, token);
    }

    private async Task<bool> AnySubjectConfirmedAfterAsync(
        NpgsqlConnection conn, SchemaIndex index, ListSubjectsRequest request,
        SortedSet<SubjectRef> subjects, string afterKey, CancellationToken ct)
    {
        foreach (var subject in subjects)
        {
            if (string.CompareOrdinal(SubjectKey(subject), afterKey) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, request.Object, request.Permission,
                subject, request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }

    private async Task CollectLeafSubjectsAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SortedSet<SubjectRef> subjects, HashSet<EvalFrame> visited, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, new SubjectRef(Nul, "<collect>"));
        if (!visited.Add(frame)) return;

        var def = index.Permission(obj.Type, permission);
        await CollectFromExprAsync(conn, index, tenant, obj, def.Expression, subjects, visited, ct);
    }

    private async Task CollectFromExprAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SortedSet<SubjectRef> subjects, HashSet<EvalFrame> visited, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
                await CollectFromRelationAsync(conn, index, tenant, obj, r.Relation, subjects, visited, ct);
                break;
            case Union u:
                await CollectFromExprAsync(conn, index, tenant, obj, u.Left, subjects, visited, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, u.Right, subjects, visited, ct);
                break;
            case Intersect i:
                await CollectFromExprAsync(conn, index, tenant, obj, i.Left, subjects, visited, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, i.Right, subjects, visited, ct);
                break;
            case Exclude e:
                await CollectFromExprAsync(conn, index, tenant, obj, e.Left, subjects, visited, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, e.Right, subjects, visited, ct);
                break;
            case Conditioned c:
                await CollectFromExprAsync(conn, index, tenant, obj, c.Inner, subjects, visited, ct);
                break;
            case Arrow a:
            {
                var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, a.Relation, ct);
                foreach (var edge in edges)
                {
                    var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
                    if (index.TryPermission(related.Type, a.Permission, out _))
                        await CollectLeafSubjectsAsync(conn, index, tenant, related, a.Permission, subjects, visited, ct);
                    else
                        await CollectFromRelationAsync(conn, index, tenant, related, a.Permission, subjects, visited, ct);
                }
                break;
            }
        }
    }

    private async Task CollectFromRelationAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SortedSet<SubjectRef> subjects, HashSet<EvalFrame> visited, CancellationToken ct)
    {
        if (!visited.Add(new EvalFrame(obj, relation, new SubjectRef(Nul, "<collect-rel>"))))
            return;

        var tuples = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, relation, ct);
        foreach (var tuple in tuples)
        {
            var s = tuple.Subject;
            if (s.IsWildcard)
                subjects.Add(new SubjectRef(s.Type, "*"));
            else if (s.IsSubjectSet)
            {
                var nested = new EntityRef(s.Type, s.Id);
                await CollectFromRelationAsync(conn, index, tenant, nested, s.Relation!, subjects, visited, ct);
            }
            else
                subjects.Add(new SubjectRef(s.Type, s.Id));
        }
    }
}
