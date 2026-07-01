using Custodex.Abstractions;

namespace Custodex.AspNetCore.Tests;

internal sealed class FakeAuthorizer : IAuthorizer
{
    private readonly CheckResult? _result;
    private readonly ListObjectsResult? _listResult;
    private readonly Exception? _exception;

    public FakeAuthorizer(CheckResult result) => _result = result;

    public FakeAuthorizer(ListObjectsResult listResult) => _listResult = listResult;

    public FakeAuthorizer(Exception exception) => _exception = exception;

    public CheckRequest? LastRequest { get; private set; }

    public ListObjectsRequest? LastListRequest { get; private set; }

    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return _exception is not null
            ? Task.FromException<CheckResult>(_exception)
            : Task.FromResult(_result ?? new CheckResult(false));
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        LastListRequest = request;
        return _exception is not null
            ? Task.FromException<ListObjectsResult>(_exception)
            : Task.FromResult(_listResult ?? new ListObjectsResult([], null));
    }

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
