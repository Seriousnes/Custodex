using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    private static readonly string Nul = ((char)0).ToString();

    private static string SubjectKey(SubjectRef s) => s.Type + Nul + s.Id;

    private static readonly IComparer<SubjectRef> SubjectOrder =
        Comparer<SubjectRef>.Create((a, b) => string.CompareOrdinal(SubjectKey(a), SubjectKey(b)));

    /// <summary>
    /// Lists the subjects (of any principal type) that hold the requested permission on the object.
    /// Forward-collects candidate concrete subjects — wildcard grants surface as the "*" subject —
    /// then confirms each with the pointwise Check so exclusion and intersection are honoured,
    /// paginating to exactly <c>PageSize</c> over the composite subject key.
    /// </summary>
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.Object.Type, request.Permission);

        var candidates = new SortedSet<SubjectRef>(SubjectOrder);
        var visited = new HashSet<EvalFrame>();
        await CollectLeafSubjectsAsync(index, request.Tenant, request.Object, request.Permission,
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
                index, request.Tenant, request.Object, request.Permission, subject, request.Context, ctx, explain: null, ct);
            if (!ok.IsTrue) continue;

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
            if (ok.IsTrue) return true;
        }
        return false;
    }

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
                if (index.TryRelation(obj.Type, r.Relation, out _))
                    await CollectFromRelationAsync(index, tenant, obj, r.Relation, subjects, visited, ct);
                else
                    await CollectLeafSubjectsAsync(index, tenant, obj, r.Relation, subjects, visited, ct);
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
