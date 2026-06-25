using Custodex.Service.Mapping;
using Custodex.V1;
using Grpc.Core;
using Contracts = Custodex.Abstractions;

namespace Custodex.Service.Services;

/// <summary>
/// gRPC service implementation for the <c>Decision</c> service.
/// Delegates all four operations to <see cref="IAuthorizer"/> via <see cref="ProtoMap"/>.
/// </summary>
public sealed class DecisionGrpcService(Contracts.IAuthorizer authorizer) : Decision.DecisionBase
{
    /// <inheritdoc />
    public override async Task<CheckResponse> Check(CheckRequest request, ServerCallContext context)
    {
        var result = await authorizer.CheckAsync(new Contracts.CheckRequest(
            ProtoMap.FromProto(request.Tenant),
            ProtoMap.FromProto(request.Object),
            request.Permission,
            ProtoMap.FromProto(request.Subject),
            ProtoMap.FromProto(request.Context),
            request.Explain), context.CancellationToken);

        var response = new CheckResponse { Allowed = result.Allowed };
        if (request.Explain && result.Explain is not null)
            response.Explain = ProtoMap.ToProto(result.Explain);
        return response;
    }

    /// <inheritdoc />
    public override async Task<BatchCheckResponse> BatchCheck(BatchCheckRequest request, ServerCallContext context)
    {
        var items = request.Items
            .Select(i => new Contracts.CheckItem(
                ProtoMap.FromProto(i.Object),
                i.Permission,
                ProtoMap.FromProto(i.Subject)))
            .ToList();

        var results = await authorizer.BatchCheckAsync(new Contracts.BatchCheckRequest(
            ProtoMap.FromProto(request.Tenant),
            items,
            ProtoMap.FromProto(request.Context)), context.CancellationToken);

        var response = new BatchCheckResponse();
        foreach (var r in results)
            response.Results.Add(new CheckResponse { Allowed = r.Allowed });
        return response;
    }

    /// <inheritdoc />
    public override async Task<ListObjectsResponse> ListObjects(ListObjectsRequest request, ServerCallContext context)
    {
        var pageSize = request.PageSize > 0 ? request.PageSize : 100;
        var token = string.IsNullOrEmpty(request.ContinuationToken) ? null : request.ContinuationToken;

        var result = await authorizer.ListObjectsAsync(new Contracts.ListObjectsRequest(
            ProtoMap.FromProto(request.Tenant),
            ProtoMap.FromProto(request.Subject),
            request.ObjectType,
            request.Permission,
            ProtoMap.FromProto(request.Context),
            pageSize, token), context.CancellationToken);

        var response = new ListObjectsResponse
        {
            ContinuationToken = result.ContinuationToken ?? string.Empty,
        };
        response.ObjectIds.AddRange(result.ObjectIds);
        return response;
    }

    /// <inheritdoc />
    public override async Task<ListSubjectsResponse> ListSubjects(ListSubjectsRequest request, ServerCallContext context)
    {
        var pageSize = request.PageSize > 0 ? request.PageSize : 100;
        var token = string.IsNullOrEmpty(request.ContinuationToken) ? null : request.ContinuationToken;

        var result = await authorizer.ListSubjectsAsync(new Contracts.ListSubjectsRequest(
            ProtoMap.FromProto(request.Tenant),
            ProtoMap.FromProto(request.Object),
            request.Permission,
            ProtoMap.FromProto(request.Context),
            pageSize, token), context.CancellationToken);

        var response = new ListSubjectsResponse
        {
            ContinuationToken = result.ContinuationToken ?? string.Empty,
        };
        response.Subjects.AddRange(result.Subjects.Select(ProtoMap.ToProto));
        return response;
    }
}
