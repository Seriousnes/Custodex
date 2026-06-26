using Custodex.Abstractions;
using Custodex.Protos;
using Custodex.Service.Tenancy;
using Custodex.Api;

using Grpc.Core;

namespace Custodex.Service.Services;

/// <summary>
/// gRPC service for store and tenant provisioning, delegating to
/// <see cref="IStoreManager"/> and <see cref="ITenantManager"/>.
/// </summary>
public sealed class ProvisioningGrpcService(IStoreManager stores, ITenantManager tenants, ITenantContextAccessor tc)
    : Provisioning.ProvisioningBase
{
    /// <inheritdoc />
    public override async Task<CreateStoreResponse> CreateStore(CreateStoreRequest request, ServerCallContext context)
    {
        if (!string.Equals(request.Store, tc.AuthenticatedStore, StringComparison.Ordinal))
            throw new RpcException(new Status(
                StatusCode.PermissionDenied, "The authenticated principal is not scoped to the targeted store."));

        await stores.CreateStoreAsync(request.Store, context.CancellationToken);
        return new CreateStoreResponse();
    }

    /// <inheritdoc />
    public override async Task<CreateTenantResponse> CreateTenant(CreateTenantRequest request, ServerCallContext context)
    {
        if (!string.Equals(request.Tenant.Store, tc.AuthenticatedStore, StringComparison.Ordinal))
            throw new RpcException(new Status(
                StatusCode.PermissionDenied, "The authenticated principal is not scoped to the targeted store."));

        await tenants.CreateTenantAsync(ProtoMap.FromProto(request.Tenant), context.CancellationToken);
        return new CreateTenantResponse();
    }
}
