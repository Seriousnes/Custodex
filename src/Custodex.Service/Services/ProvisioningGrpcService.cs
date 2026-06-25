using Custodex.Abstractions;
using Custodex.Service.Mapping;
using Custodex.V1;
using Grpc.Core;

namespace Custodex.Service.Services;

/// <summary>
/// gRPC service for store and tenant provisioning, delegating to
/// <see cref="IStoreManager"/> and <see cref="ITenantManager"/>.
/// </summary>
public sealed class ProvisioningGrpcService(IStoreManager stores, ITenantManager tenants)
    : Provisioning.ProvisioningBase
{
    /// <inheritdoc />
    public override async Task<CreateStoreResponse> CreateStore(CreateStoreRequest request, ServerCallContext context)
    {
        await stores.CreateStoreAsync(request.Store, context.CancellationToken);
        return new CreateStoreResponse();
    }

    /// <inheritdoc />
    public override async Task<CreateTenantResponse> CreateTenant(CreateTenantRequest request, ServerCallContext context)
    {
        await tenants.CreateTenantAsync(ProtoMap.FromProto(request.Tenant), context.CancellationToken);
        return new CreateTenantResponse();
    }
}
