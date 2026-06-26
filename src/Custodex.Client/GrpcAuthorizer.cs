using Custodex.Abstractions;
using Custodex.Client.Transport;
using Custodex.Protos;

using Proto = Custodex.Api;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="IAuthorizer"/> by forwarding decisions to a remote
/// <c>Custodex.Service</c> over gRPC. Typed engine exceptions are preserved
/// across the wire via <see cref="RemoteStatus"/>.
/// </summary>
public sealed class GrpcAuthorizer(Proto.Decision.DecisionClient client) : IAuthorizer
{
    /// <inheritdoc/>
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var proto = new Proto.CheckRequest
        {
            Object = ProtoMap.ToProto(request.Object),
            Permission = request.Permission,
            Subject = ProtoMap.ToProto(request.Subject),
            Context = ProtoMap.ToProto(request.Context),
            Explain = request.Explain,
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.CheckAsync(proto, headers: ClientHeaders.TenantMeta(request.Tenant), cancellationToken: ct).ResponseAsync);
        return new CheckResult(
            response.Allowed,
            response.Explain is { Description.Length: > 0 } ? ProtoMap.FromProto(response.Explain) : null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        var proto = new Proto.BatchCheckRequest
        {
            Context = ProtoMap.ToProto(request.Context),
        };
        foreach (var item in request.Items)
        {
            proto.Items.Add(new Proto.CheckItem
            {
                Object = ProtoMap.ToProto(item.Object),
                Permission = item.Permission,
                Subject = ProtoMap.ToProto(item.Subject),
            });
        }
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.BatchCheckAsync(proto, headers: ClientHeaders.TenantMeta(request.Tenant), cancellationToken: ct).ResponseAsync);
        return response.Results
            .Select(r => new CheckResult(
                r.Allowed,
                r.Explain is { Description.Length: > 0 } ? ProtoMap.FromProto(r.Explain) : null))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var proto = new Proto.ListObjectsRequest
        {
            Subject = ProtoMap.ToProto(request.Subject),
            ObjectType = request.ObjectType,
            Permission = request.Permission,
            Context = ProtoMap.ToProto(request.Context),
            PageSize = request.PageSize,
            ContinuationToken = request.ContinuationToken ?? string.Empty,
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.ListObjectsAsync(proto, headers: ClientHeaders.TenantMeta(request.Tenant), cancellationToken: ct).ResponseAsync);
        return new ListObjectsResult(
            response.ObjectIds.ToList(),
            string.IsNullOrEmpty(response.ContinuationToken) ? null : response.ContinuationToken);
    }

    /// <inheritdoc/>
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var proto = new Proto.ListSubjectsRequest
        {
            Object = ProtoMap.ToProto(request.Object),
            Permission = request.Permission,
            Context = ProtoMap.ToProto(request.Context),
            PageSize = request.PageSize,
            ContinuationToken = request.ContinuationToken ?? string.Empty,
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.ListSubjectsAsync(proto, headers: ClientHeaders.TenantMeta(request.Tenant), cancellationToken: ct).ResponseAsync);
        return new ListSubjectsResult(
            response.Subjects.Select(ProtoMap.FromProto).ToList(),
            string.IsNullOrEmpty(response.ContinuationToken) ? null : response.ContinuationToken);
    }
}
