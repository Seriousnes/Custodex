using Custodex.Abstractions;

namespace Custodex.Blazor.Tests;

internal sealed class CountingAuthorizer(bool allowed) : IAuthorizer
{
    private int _checkCalls;

    public int CheckCalls => Volatile.Read(ref _checkCalls);

    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _checkCalls);
        return Task.FromResult(new CheckResult(allowed));
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default) =>
        Task.FromResult(new ListObjectsResult([], null));

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
