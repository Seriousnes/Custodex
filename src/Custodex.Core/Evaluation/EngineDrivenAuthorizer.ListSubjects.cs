using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    // Subjects are ordered by a composite (type, id) ordinal key so pagination is stable across
    // principal types. The separator is NUL (built via (char)0 to keep the source free of control
    // bytes): it makes a type that is a prefix of another still order before it, and keeps distinct
    // (type, id) pairs from colliding on one key. The key is also what the continuation cursor encodes.
    private static readonly string Nul = ((char)0).ToString();
    private static string SubjectKey(SubjectRef s) => s.Type + Nul + s.Id;
    private static readonly IComparer<SubjectRef> SubjectOrder =
        Comparer<SubjectRef>.Create((a, b) => string.CompareOrdinal(SubjectKey(a), SubjectKey(b)));

    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.Object.Type, request.Permission);   // validate

        // Forward-collect candidate concrete subjects (of any principal type) from the whole
        // permission expansion. A wildcard subject (type:*) is collected as a "*" candidate that
        // flows through the same confirm + paginate loop; an exclusion on type:* still denies it.
        var candidates = new SortedSet<SubjectRef>(SubjectOrder);
        var visited = new HashSet<EvalFrame>();
        await CollectLeafSubjectsAsync(index, request.Tenant, request.Object, request.Permission,
            candidates, visited, ct);

        // Confirm each candidate with the pointwise Check (honours exclusion/intersection).
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
                index, request.Tenant, request.Object, request.Permission, subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(subject);
            lastKey = key;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = !await AnySubjectConfirmedAfterAsync(index, request, candidates, key, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastKey!);
        return new ListSubjectsResult(confirmed, token);
    }

    private async Task<bool> AnySubjectConfirmedAfterAsync(
        SchemaIndex index, ListSubjectsRequest request, SortedSet<SubjectRef> subjects, string afterKey, CancellationToken ct)
    {
        foreach (var subject in subjects)
        {
            if (string.CompareOrdinal(SubjectKey(subject), afterKey) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                index, request.Tenant, request.Object, request.Permission,
                subject, request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }

    /// <summary>
    /// Walks a permission expression forward, collecting every concrete leaf subject
    /// (of any principal type) reachable through relations, nested group membership, and
    /// arrow targets. A wildcard subject (type:*) is collected as a "*" candidate.
    /// Cycle-guarded via <paramref name="visited"/>.
    /// </summary>
    private async Task CollectLeafSubjectsAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SortedSet<SubjectRef> subjects, HashSet<EvalFrame> visited, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, new SubjectRef(Nul, "<collect>"));
        if (!visited.Add(frame)) return;

        var def = index.Permission(obj.Type, permission);
        await CollectFromExprAsync(index, tenant, obj, def.Expression, subjects, visited, ct);
    }

    private async Task CollectFromExprAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SortedSet<SubjectRef> subjects, HashSet<EvalFrame> visited, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
                await CollectFromRelationAsync(index, tenant, obj, r.Relation, subjects, visited, ct);
                break;
            case Union u:
                await CollectFromExprAsync(index, tenant, obj, u.Left, subjects, visited, ct);
                await CollectFromExprAsync(index, tenant, obj, u.Right, subjects, visited, ct);
                break;
            case Intersect i:
                await CollectFromExprAsync(index, tenant, obj, i.Left, subjects, visited, ct);
                await CollectFromExprAsync(index, tenant, obj, i.Right, subjects, visited, ct);
                break;
            case Exclude e:
                // Collect candidates from both sides; the confirm-by-Check step applies the exclusion.
                await CollectFromExprAsync(index, tenant, obj, e.Left, subjects, visited, ct);
                await CollectFromExprAsync(index, tenant, obj, e.Right, subjects, visited, ct);
                break;
            case Conditioned c:
                await CollectFromExprAsync(index, tenant, obj, c.Inner, subjects, visited, ct);
                break;
            case Arrow a:
            {
                var edges = await _relations.GetByObjectAsync(tenant, obj, a.Relation, ct);
                foreach (var edge in edges)
                {
                    var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
                    if (index.TryPermission(related.Type, a.Permission, out _))
                        await CollectLeafSubjectsAsync(index, tenant, related, a.Permission, subjects, visited, ct);
                    else
                        await CollectFromRelationAsync(index, tenant, related, a.Permission, subjects, visited, ct);
                }
                break;
            }
        }
    }

    private async Task CollectFromRelationAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SortedSet<SubjectRef> subjects, HashSet<EvalFrame> visited, CancellationToken ct)
    {
        // Cycle guard for relation recursion. A distinct sentinel subject keeps these relation
        // frames from colliding with the permission frames added in CollectLeafSubjectsAsync.
        // Global dedup is correct for collection: each (object, relation) contributes the same
        // candidate subjects on every visit, so visiting once gathers the complete superset.
        if (!visited.Add(new EvalFrame(obj, relation, new SubjectRef(Nul, "<collect-rel>"))))
            return;

        var tuples = await _relations.GetByObjectAsync(tenant, obj, relation, ct);
        foreach (var tuple in tuples)
        {
            var s = tuple.Subject;
            if (s.IsWildcard)
                subjects.Add(new SubjectRef(s.Type, "*"));
            else if (s.IsSubjectSet)
            {
                var nested = new EntityRef(s.Type, s.Id);
                await CollectFromRelationAsync(index, tenant, nested, s.Relation!, subjects, visited, ct);
            }
            else
                subjects.Add(new SubjectRef(s.Type, s.Id));
        }
    }
}
