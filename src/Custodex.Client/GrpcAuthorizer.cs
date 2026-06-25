using Custodex.Abstractions;
using Custodex.Client.Mapping;
using Custodex.Client.Transport;
using ProtoV1 = Custodex.V1;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="IAuthorizer"/> by forwarding decisions to a remote
/// <c>Custodex.Service</c> over gRPC. Typed engine exceptions are preserved
/// across the wire via <see cref="RemoteStatus"/>.
/// </summary>
public sealed class GrpcAuthorizer(ProtoV1.Decision.DecisionClient client) : IAuthorizer
{
    /// <inheritdoc/>
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var proto = new ProtoV1.CheckRequest
        {
            Tenant = ProtoMapping.ToProto(request.Tenant),
            Object = ProtoMapping.ToProto(request.Object),
            Permission = request.Permission,
            Subject = ProtoMapping.ToProto(request.Subject),
            Context = ProtoMapping.ToProto(request.Context),
            Explain = request.Explain,
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.CheckAsync(proto, cancellationToken: ct).ResponseAsync);
        return new CheckResult(
            response.Allowed,
            response.Explain is { Description.Length: > 0 } ? ProtoMapping.ToDomain(response.Explain) : null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        var proto = new ProtoV1.BatchCheckRequest
        {
            Tenant = ProtoMapping.ToProto(request.Tenant),
            Context = ProtoMapping.ToProto(request.Context),
        };
        foreach (var item in request.Items)
        {
            proto.Items.Add(new ProtoV1.CheckItem
            {
                Object = ProtoMapping.ToProto(item.Object),
                Permission = item.Permission,
                Subject = ProtoMapping.ToProto(item.Subject),
            });
        }
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.BatchCheckAsync(proto, cancellationToken: ct).ResponseAsync);
        return response.Results
            .Select(r => new CheckResult(
                r.Allowed,
                r.Explain is { Description.Length: > 0 } ? ProtoMapping.ToDomain(r.Explain) : null))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var proto = new ProtoV1.ListObjectsRequest
        {
            Tenant = ProtoMapping.ToProto(request.Tenant),
            Subject = ProtoMapping.ToProto(request.Subject),
            ObjectType = request.ObjectType,
            Permission = request.Permission,
            Context = ProtoMapping.ToProto(request.Context),
            PageSize = request.PageSize,
            ContinuationToken = request.ContinuationToken ?? string.Empty,
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.ListObjectsAsync(proto, cancellationToken: ct).ResponseAsync);
        return new ListObjectsResult(
            response.ObjectIds.ToList(),
            string.IsNullOrEmpty(response.ContinuationToken) ? null : response.ContinuationToken);
    }

    /// <inheritdoc/>
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var proto = new ProtoV1.ListSubjectsRequest
        {
            Tenant = ProtoMapping.ToProto(request.Tenant),
            Object = ProtoMapping.ToProto(request.Object),
            Permission = request.Permission,
            Context = ProtoMapping.ToProto(request.Context),
            PageSize = request.PageSize,
            ContinuationToken = request.ContinuationToken ?? string.Empty,
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.ListSubjectsAsync(proto, cancellationToken: ct).ResponseAsync);
        return new ListSubjectsResult(
            response.Subjects.Select(ProtoMapping.ToDomain).ToList(),
            string.IsNullOrEmpty(response.ContinuationToken) ? null : response.ContinuationToken);
    }
}
