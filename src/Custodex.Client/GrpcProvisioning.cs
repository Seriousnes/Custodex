using Custodex.Abstractions;
using Custodex.Client.Transport;
using Custodex.Protos;
using ProtoV1 = Custodex.V1;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="IStoreManager"/> via the gRPC <c>Provisioning</c> service.
/// </summary>
public sealed class GrpcStoreManager(ProtoV1.Provisioning.ProvisioningClient client) : IStoreManager
{
    /// <inheritdoc/>
    public async Task CreateStoreAsync(string store, CancellationToken ct = default)
    {
        var proto = new ProtoV1.CreateStoreRequest { Store = store };
        await RemoteStatus.UnwrapAsync(() =>
            client.CreateStoreAsync(proto, cancellationToken: ct).ResponseAsync);
    }
}

/// <summary>
/// Implements <see cref="ITenantManager"/> via the gRPC <c>Provisioning</c> service.
/// </summary>
public sealed class GrpcTenantManager(ProtoV1.Provisioning.ProvisioningClient client) : ITenantManager
{
    /// <inheritdoc/>
    public async Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
    {
        var proto = new ProtoV1.CreateTenantRequest { Tenant = ProtoMap.ToProto(tenant) };
        await RemoteStatus.UnwrapAsync(() =>
            client.CreateTenantAsync(proto, cancellationToken: ct).ResponseAsync);
    }
}
