using Custodex.Abstractions;
using Custodex.Client.Transport;
using Custodex.Protos;

using Proto = Custodex.Api;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="IStoreManager"/> via the gRPC <c>Provisioning</c> service.
/// </summary>
public sealed class GrpcStoreManager(Proto.Provisioning.ProvisioningClient client) : IStoreManager
{
    /// <inheritdoc/>
    public async Task CreateStoreAsync(string store, CancellationToken ct = default)
    {
        var proto = new Proto.CreateStoreRequest { Store = store };
        await RemoteStatus.UnwrapAsync(() =>
            client.CreateStoreAsync(proto, cancellationToken: ct).ResponseAsync);
    }
}

/// <summary>
/// Implements <see cref="ITenantManager"/> via the gRPC <c>Provisioning</c> service.
/// </summary>
public sealed class GrpcTenantManager(Proto.Provisioning.ProvisioningClient client) : ITenantManager
{
    /// <inheritdoc/>
    public async Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
    {
        var proto = new Proto.CreateTenantRequest { Tenant = ProtoMap.ToProto(tenant) };
        await RemoteStatus.UnwrapAsync(() =>
            client.CreateTenantAsync(proto, cancellationToken: ct).ResponseAsync);
    }
}
