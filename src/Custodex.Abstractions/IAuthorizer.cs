namespace Custodex.Abstractions;

public interface IAuthorizer
{
    Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default);
    Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default);
    Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default);
}
