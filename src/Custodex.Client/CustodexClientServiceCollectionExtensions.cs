using Custodex.Abstractions;

using Grpc.Net.Client;

using Microsoft.Extensions.DependencyInjection;

using ProtoV1 = Custodex.V1;

namespace Custodex.Client;

/// <summary>
/// Extension methods for registering the Custodex gRPC client with <see cref="IServiceCollection"/>.
/// </summary>
public static class CustodexClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="GrpcChannel"/> and all five Custodex facades against the
    /// <see cref="IAuthorizer"/>, <see cref="IRelationManager"/>, <see cref="ISchemaManager"/>,
    /// <see cref="IStoreManager"/>, and <see cref="ITenantManager"/> interfaces.
    /// Swap in-process evaluation for the remote service by replacing
    /// <c>AddCustodex().UsePostgres(…)</c> with this call — all other code is unchanged.
    /// </summary>
    public static IServiceCollection AddCustodexClient(this IServiceCollection services, string address) =>
        services.AddCustodexClient(new Uri(address));

    /// <summary>
    /// Registers a <see cref="GrpcChannel"/> and all five Custodex facades using the
    /// provided <paramref name="address"/> URI.
    /// </summary>
    public static IServiceCollection AddCustodexClient(this IServiceCollection services, Uri address)
    {
        services.AddSingleton(_ => GrpcChannel.ForAddress(address));
        services.AddSingleton(sp => new ProtoV1.Decision.DecisionClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(sp => new ProtoV1.Relations.RelationsClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(sp => new ProtoV1.Schema.SchemaClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(sp => new ProtoV1.Provisioning.ProvisioningClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton<IAuthorizer>(sp =>
            new GrpcAuthorizer(sp.GetRequiredService<ProtoV1.Decision.DecisionClient>()));
        services.AddSingleton<IRelationManager>(sp =>
            new GrpcRelationManager(sp.GetRequiredService<ProtoV1.Relations.RelationsClient>()));
        services.AddSingleton<ISchemaManager>(sp =>
            new GrpcSchemaManager(sp.GetRequiredService<ProtoV1.Schema.SchemaClient>()));
        services.AddSingleton<IStoreManager>(sp =>
            new GrpcStoreManager(sp.GetRequiredService<ProtoV1.Provisioning.ProvisioningClient>()));
        services.AddSingleton<ITenantManager>(sp =>
            new GrpcTenantManager(sp.GetRequiredService<ProtoV1.Provisioning.ProvisioningClient>()));
        return services;
    }
}
