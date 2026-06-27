using Custodex.Abstractions;
using Custodex.Core.Evaluation;

using Npgsql;

namespace Custodex.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    /// <inheritdoc />
    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.ObjectType, request.Permission);

        return await RunAsync(async conn =>
        {
            var reachable = await CteCandidates.ReachableObjectIdsAsync(conn, BoundTx, request.Tenant, request.Subject, request.ObjectType, ct);
            var universe = await CteCandidates.TypeUniverseAsync(conn, BoundTx, request.Tenant, request.ObjectType, ct);
            var candidates = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var id in reachable) candidates.Add(id);
            foreach (var id in universe) candidates.Add(id);

            var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
            var confirmed = new List<string>(request.PageSize);
            string? lastConfirmed = null;
            var exhausted = true;

            foreach (var id in candidates)
            {
                if (after is not null && string.CompareOrdinal(id, after) <= 0) continue;

                var obj = new EntityRef(request.ObjectType, id);
                var ctx = new EvalContext(_options);
                var ok = await CheckPermissionAsync(
                    conn, index, request.Tenant, obj, request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
                if (!ok) continue;

                confirmed.Add(id);
                lastConfirmed = id;
                if (confirmed.Count == request.PageSize)
                {
                    exhausted = !await AnyConfirmedAfterAsync(conn, index, request, candidates, id, ct);
                    break;
                }
            }

            var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
            return new ListObjectsResult(confirmed, token);
        }, ct);
    }

    private async Task<bool> AnyConfirmedAfterAsync(
        NpgsqlConnection conn, SchemaIndex index, ListObjectsRequest request, SortedSet<string> candidates,
        string afterId, CancellationToken ct)
    {
        foreach (var id in candidates)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, new EntityRef(request.ObjectType, id),
                request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }
}
