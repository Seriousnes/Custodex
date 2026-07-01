using Custodex.Abstractions;

namespace Custodex.Blazor.Tests;

internal sealed class RecordingAuthorizer(CheckResult result, ListObjectsResult? listResult = null) : IAuthorizer
{
    public CheckRequest? Last { get; private set; }

    public ListObjectsRequest? LastList { get; private set; }

    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        Last = request;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        LastList = request;
        return Task.FromResult(listResult ?? new ListObjectsResult([], null));
    }

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
