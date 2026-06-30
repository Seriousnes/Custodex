using Custodex.Abstractions;

namespace Custodex.Blazor.Tests;

internal sealed class RecordingAuthorizer(CheckResult result) : IAuthorizer
{
    public CheckRequest? Last { get; private set; }

    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        Last = request;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
