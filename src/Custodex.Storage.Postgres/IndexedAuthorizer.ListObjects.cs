using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

public sealed partial class IndexedAuthorizer
{
    private const int ScanBatchSize = 256;

    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var schema = await _schemaStore.GetActiveAsync(request.Tenant.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{request.Tenant.Store}'>");

        if (!await _index.IsBuiltAsync(request.Tenant, schema.Version, ct))
            return await _inner.ListObjectsAsync(request, ct);

        var subject = IndexSubject.Of(request.Subject);
        var wildcard = $"{request.Subject.Type}:*";

        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        await CollectConfirmedAsync(request, schema.Version, subject, candidates, ct);
        if (!string.Equals(wildcard, subject, StringComparison.Ordinal))
            await CollectConfirmedAsync(request, schema.Version, wildcard, candidates, ct);

        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<string>(request.PageSize);
        string? lastConfirmed = null;
        var exhausted = true;

        foreach (var id in candidates)
        {
            if (after is not null && string.CompareOrdinal(id, after) <= 0)
                continue;
            confirmed.Add(id);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = string.CompareOrdinal(candidates.Max!, id) <= 0;
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListObjectsResult(confirmed, token);
    }

    private async Task CollectConfirmedAsync(
        ListObjectsRequest request, string schemaVersion, string subject, SortedSet<string> candidates, CancellationToken ct)
    {
        string? cursor = null;
        while (true)
        {
            var batch = await _index.QueryObjectsAsync(
                request.Tenant, schemaVersion, subject, request.Permission, request.ObjectType,
                ScanBatchSize, cursor, ct);
            if (batch.Count == 0)
                break;
            foreach (var row in batch)
            {
                cursor = row.ObjectId;
                if (await ConfirmAsync(request, row, ct))
                    candidates.Add(row.ObjectId);
            }
            if (batch.Count < ScanBatchSize)
                break;
        }
    }

    private async Task<bool> ConfirmAsync(ListObjectsRequest request, ReverseIndexRow row, CancellationToken ct)
    {
        if (!row.Conditioned)
            return true;
        var result = await _inner.CheckAsync(new CheckRequest(
            request.Tenant, new EntityRef(request.ObjectType, row.ObjectId),
            request.Permission, request.Subject, request.Context), ct);
        return result.Allowed;
    }
}
