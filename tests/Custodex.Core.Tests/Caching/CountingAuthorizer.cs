using Custodex.Abstractions;

namespace Custodex.Core.Tests.Caching;

internal sealed class CountingAuthorizer(Func<CheckRequest, CheckResult> decide) : IAuthorizer
{
    private int _checkCalls;
    private int _listCalls;

    public int CheckCalls => Volatile.Read(ref _checkCalls);

    public int ListCalls => Volatile.Read(ref _listCalls);

    public Func<CheckRequest, Task>? BeforeReturn { get; set; }

    public Func<int, Exception?>? ThrowOn { get; set; }

    public static CountingAuthorizer Returning(bool allowed) =>
        new(_ => new CheckResult(allowed));

    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref _checkCalls);
        var toThrow = ThrowOn?.Invoke(call);
        if (toThrow is not null)
            throw toThrow;
        if (BeforeReturn is not null)
            await BeforeReturn(request);
        return decide(request);
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _listCalls);
        return Task.FromResult(new ListObjectsResult([], null));
    }

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
