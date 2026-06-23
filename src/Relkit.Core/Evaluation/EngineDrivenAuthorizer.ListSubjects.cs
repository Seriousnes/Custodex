using Relkit.Abstractions;

namespace Relkit.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.Object.Type, request.Permission);   // validate

        // Forward-collect candidate leaf users from the whole permission expansion.
        var candidateUsers = new SortedSet<string>(StringComparer.Ordinal);
        var sawWildcardUser = false;
        var visited = new HashSet<EvalFrame>();
        await CollectLeafUsersAsync(index, request.Tenant, request.Object, request.Permission,
            candidateUsers, visited, v => sawWildcardUser = true, ct);

        // A public grant surfaces as the wildcard subject "*": add it to the candidate set so it
        // flows through the same confirm + paginate loop (no special-casing, exact page sizes).
        // "*" (0x2A) sorts first ordinal; CheckPermissionAsync(user:*) returns the public-grant
        // truth and an exclusion on user:* still denies it correctly.
        if (sawWildcardUser) candidateUsers.Add("*");

        // Confirm each candidate with the pointwise Check (honours exclusion/intersection).
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
                index, request.Tenant, request.Object, request.Permission, subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(subject);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = !await AnySubjectConfirmedAfterAsync(index, request, candidateUsers, id, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListSubjectsResult(confirmed, token);
    }

    private async Task<bool> AnySubjectConfirmedAfterAsync(
        SchemaIndex index, ListSubjectsRequest request, SortedSet<string> users, string afterId, CancellationToken ct)
    {
        foreach (var id in users)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                index, request.Tenant, request.Object, request.Permission,
                new SubjectRef("user", id), request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }

    /// <summary>
    /// Walks a permission expression forward, collecting every concrete leaf <c>user</c>
    /// reachable through relations, nested group membership, and arrow targets. Records
    /// whether a <c>user:*</c> wildcard was seen. Cycle-guarded via <paramref name="visited"/>.
    /// </summary>
    private async Task CollectLeafUsersAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SortedSet<string> users, HashSet<EvalFrame> visited, Action<bool> onWildcardUser, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, new SubjectRef("user", "<collect>"));
        if (!visited.Add(frame)) return;

        var def = index.Permission(obj.Type, permission);
        await CollectFromExprAsync(index, tenant, obj, def.Expression, users, visited, onWildcardUser, ct);
    }

    private async Task CollectFromExprAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SortedSet<string> users, HashSet<EvalFrame> visited, Action<bool> onWildcardUser, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
                await CollectFromRelationAsync(index, tenant, obj, r.Relation, users, visited, onWildcardUser, ct);
                break;
            case Union u:
                await CollectFromExprAsync(index, tenant, obj, u.Left, users, visited, onWildcardUser, ct);
                await CollectFromExprAsync(index, tenant, obj, u.Right, users, visited, onWildcardUser, ct);
                break;
            case Intersect i:
                await CollectFromExprAsync(index, tenant, obj, i.Left, users, visited, onWildcardUser, ct);
                await CollectFromExprAsync(index, tenant, obj, i.Right, users, visited, onWildcardUser, ct);
                break;
            case Exclude e:
                // Collect candidates from both sides; the confirm-by-Check step applies the exclusion.
                await CollectFromExprAsync(index, tenant, obj, e.Left, users, visited, onWildcardUser, ct);
                await CollectFromExprAsync(index, tenant, obj, e.Right, users, visited, onWildcardUser, ct);
                break;
            case Conditioned c:
                await CollectFromExprAsync(index, tenant, obj, c.Inner, users, visited, onWildcardUser, ct);
                break;
            case Arrow a:
            {
                var edges = await _relations.GetByObjectAsync(tenant, obj, a.Relation, ct);
                foreach (var edge in edges)
                {
                    var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
                    if (index.TryPermission(related.Type, a.Permission, out _))
                        await CollectLeafUsersAsync(index, tenant, related, a.Permission, users, visited, onWildcardUser, ct);
                    else
                        await CollectFromRelationAsync(index, tenant, related, a.Permission, users, visited, onWildcardUser, ct);
                }
                break;
            }
        }
    }

    private async Task CollectFromRelationAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SortedSet<string> users, HashSet<EvalFrame> visited, Action<bool> onWildcardUser, CancellationToken ct)
    {
        var tuples = await _relations.GetByObjectAsync(tenant, obj, relation, ct);
        foreach (var tuple in tuples)
        {
            var s = tuple.Subject;
            if (s.IsWildcard && string.Equals(s.Type, "user", StringComparison.Ordinal))
                onWildcardUser(true);
            else if (!s.IsSubjectSet && string.Equals(s.Type, "user", StringComparison.Ordinal))
                users.Add(s.Id);
            else if (s.IsSubjectSet)
            {
                var nested = new EntityRef(s.Type, s.Id);
                await CollectFromRelationAsync(index, tenant, nested, s.Relation!, users, visited, onWildcardUser, ct);
            }
        }
    }
}
