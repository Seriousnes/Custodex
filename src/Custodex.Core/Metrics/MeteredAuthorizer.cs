using System.Diagnostics;

using Custodex.Abstractions;

namespace Custodex.Core;

internal sealed class MeteredAuthorizer(IAuthorizer inner) : IAuthorizer
{
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await inner.CheckAsync(request, ct);
        }
        finally
        {
            CustodexDiagnostics.CheckDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        inner.BatchCheckAsync(request, ct);

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default) =>
        inner.ListObjectsAsync(request, ct);

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        inner.ListSubjectsAsync(request, ct);
}
