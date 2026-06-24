using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.ObjectType, request.Permission);   // validate: throws on unknown type/permission

        // Candidate superset: reverse-reachable ∪ type universe (covers wildcard grants).
        var reachable = await CandidateObjectsAsync(request.Tenant, request.Subject, request.ObjectType, ct);
        var universeIds = await _relations.ListObjectIdsAsync(request.Tenant, request.ObjectType, ct);

        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in reachable) candidates.Add(r.Id);
        foreach (var u in universeIds) candidates.Add(u);

        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<string>(request.PageSize);
        string? lastConfirmed = null;
        var exhausted = true;

        foreach (var id in candidates)
        {
            if (after is not null && string.CompareOrdinal(id, after) <= 0) continue;   // resume strictly after cursor

            var obj = new EntityRef(request.ObjectType, id);
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                index, request.Tenant, obj, request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(id);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                // Determine whether any confirmable candidate remains beyond this page.
                exhausted = !await AnyConfirmedAfterAsync(index, request, candidates, id, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListObjectsResult(confirmed, token);
    }

    /// <summary>True if at least one candidate strictly after <paramref name="afterId"/> confirms the permission.</summary>
    private async Task<bool> AnyConfirmedAfterAsync(
        SchemaIndex index, ListObjectsRequest request, SortedSet<string> candidates, string afterId, CancellationToken ct)
    {
        foreach (var id in candidates)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                index, request.Tenant, new EntityRef(request.ObjectType, id),
                request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }
}
