using Npgsql;
using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    /// <inheritdoc />
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.Object.Type, request.Permission);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var candidateUsers = new SortedSet<string>(StringComparer.Ordinal);
        var sawWildcard = new bool[1];
        var visited = new HashSet<EvalFrame>();
        await CollectLeafUsersAsync(conn, index, request.Tenant, request.Object, request.Permission,
            candidateUsers, visited, sawWildcard, ct);
        if (sawWildcard[0]) candidateUsers.Add("*");

        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<SubjectRef>(request.PageSize);
        string? lastConfirmed = null;
        var exhausted = true;

        foreach (var id in candidateUsers)
        {
            if (after is not null && string.CompareOrdinal(id, after) <= 0) continue;
            var subject = new SubjectRef("user", id);
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, request.Object, request.Permission, subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(subject);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = !await AnySubjectConfirmedAfterAsync(conn, index, request, candidateUsers, id, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListSubjectsResult(confirmed, token);
    }

    /// <summary>True if at least one candidate user id strictly after <paramref name="afterId"/> confirms the permission.</summary>
    private async Task<bool> AnySubjectConfirmedAfterAsync(
        NpgsqlConnection conn, SchemaIndex index, ListSubjectsRequest request, SortedSet<string> users,
        string afterId, CancellationToken ct)
    {
        foreach (var id in users)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, request.Object, request.Permission,
                new SubjectRef("user", id), request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }

    private async Task CollectLeafUsersAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SortedSet<string> users, HashSet<EvalFrame> visited, bool[] sawWildcard, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, new SubjectRef("user", "<collect>"));
        if (!visited.Add(frame)) return;
        var def = index.Permission(obj.Type, permission);
        await CollectFromExprAsync(conn, index, tenant, obj, def.Expression, users, visited, sawWildcard, ct);
    }

    private async Task CollectFromExprAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SortedSet<string> users, HashSet<EvalFrame> visited, bool[] sawWildcard, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
                await CollectFromRelationAsync(conn, index, tenant, obj, r.Relation, users, visited, sawWildcard, ct);
                break;
            case Union u:
                await CollectFromExprAsync(conn, index, tenant, obj, u.Left, users, visited, sawWildcard, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, u.Right, users, visited, sawWildcard, ct);
                break;
            case Intersect i:
                await CollectFromExprAsync(conn, index, tenant, obj, i.Left, users, visited, sawWildcard, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, i.Right, users, visited, sawWildcard, ct);
                break;
            case Exclude e:
                await CollectFromExprAsync(conn, index, tenant, obj, e.Left, users, visited, sawWildcard, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, e.Right, users, visited, sawWildcard, ct);
                break;
            case Conditioned c:
                await CollectFromExprAsync(conn, index, tenant, obj, c.Inner, users, visited, sawWildcard, ct);
                break;
            case Arrow a:
            {
                var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, a.Relation, ct);
                foreach (var edge in edges)
                {
                    var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
                    if (index.TryPermission(related.Type, a.Permission, out _))
                        await CollectLeafUsersAsync(conn, index, tenant, related, a.Permission, users, visited, sawWildcard, ct);
                    else
                        await CollectFromRelationAsync(conn, index, tenant, related, a.Permission, users, visited, sawWildcard, ct);
                }
                break;
            }
        }
    }

    private async Task CollectFromRelationAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SortedSet<string> users, HashSet<EvalFrame> visited, bool[] sawWildcard, CancellationToken ct)
    {
        var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, relation, ct);
        foreach (var edge in edges)
        {
            var s = edge.Subject;
            if (s.IsWildcard && string.Equals(s.Type, "user", StringComparison.Ordinal))
                sawWildcard[0] = true;
            else if (!s.IsSubjectSet && string.Equals(s.Type, "user", StringComparison.Ordinal))
                users.Add(s.Id);
            else if (s.IsSubjectSet)
                await CollectFromRelationAsync(conn, index, tenant, new EntityRef(s.Type, s.Id), s.Relation!, users, visited, sawWildcard, ct);
        }
    }
}
