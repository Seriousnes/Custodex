using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

public sealed partial class IndexedAuthorizer
{
    /// <inheritdoc />
    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var schema = await _schemaStore.GetActiveAsync(request.Tenant.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{request.Tenant.Store}'>");

        if (!await _index.IsBuiltAsync(request.Tenant, schema.Version, ct))
            return await _inner.ListObjectsAsync(request, ct);

        var subject = IndexSubject.Of(request.Subject);
        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<string>(request.PageSize);
        string? lastConfirmed = null;
        var exhausted = true;
        var batchSize = request.PageSize + 1;
        string? cursor = after;

        while (true)
        {
            var batch = await _index.QueryObjectsAsync(
                request.Tenant, schema.Version, subject, request.Permission, request.ObjectType,
                batchSize, cursor, ct);
            if (batch.Count == 0)
                break;

            var filled = false;
            foreach (var row in batch)
            {
                cursor = row.ObjectId;
                if (!await ConfirmRowAsync(request, row, ct))
                    continue;
                confirmed.Add(row.ObjectId);
                lastConfirmed = row.ObjectId;
                if (confirmed.Count == request.PageSize)
                {
                    exhausted = !await AnyConfirmedAfterAsync(request, schema.Version, subject, row.ObjectId, ct);
                    filled = true;
                    break;
                }
            }

            if (filled)
                break;
            if (batch.Count < batchSize)
                break;
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListObjectsResult(confirmed, token);
    }

    private async Task<bool> ConfirmRowAsync(ListObjectsRequest request, ReverseIndexRow row, CancellationToken ct)
    {
        if (!row.Conditioned)
            return true;

        var result = await _inner.CheckAsync(new CheckRequest(
            request.Tenant, new EntityRef(request.ObjectType, row.ObjectId),
            request.Permission, request.Subject, request.Context), ct);
        return result.Allowed;
    }

    private async Task<bool> AnyConfirmedAfterAsync(
        ListObjectsRequest request, string schemaVersion, string subject, string afterId, CancellationToken ct)
    {
        string? cursor = afterId;
        while (true)
        {
            var batch = await _index.QueryObjectsAsync(
                request.Tenant, schemaVersion, subject, request.Permission, request.ObjectType,
                request.PageSize, cursor, ct);
            if (batch.Count == 0)
                return false;

            foreach (var row in batch)
            {
                cursor = row.ObjectId;
                if (await ConfirmRowAsync(request, row, ct))
                    return true;
            }

            if (batch.Count < request.PageSize)
                return false;
        }
    }
}
