using Custodex.Abstractions;

using Grpc.Net.Client;

using Microsoft.Extensions.DependencyInjection;

using Proto = Custodex.Api;

namespace Custodex.Client;

/// <summary>
/// Extension methods for registering the Custodex gRPC client with <see cref="IServiceCollection"/>.
/// </summary>
public static class CustodexClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="GrpcChannel"/> and the Custodex facades against the
    /// <see cref="IAuthorizer"/>, <see cref="IRelationManager"/>, <see cref="ISchemaManager"/>,
    /// <see cref="IStoreManager"/>, <see cref="ITenantManager"/>, and <see cref="IMetricsSnapshotProvider"/>
    /// interfaces. Swap in-process evaluation for the remote service by replacing
    /// <c>AddCustodex().UsePostgres(…)</c> with this call — all other code is unchanged.
    /// </summary>
    public static IServiceCollection AddCustodexClient(this IServiceCollection services, string address) =>
        services.AddCustodexClient(new Uri(address));

    /// <summary>
    /// Registers a <see cref="GrpcChannel"/> and the Custodex facades using the
    /// provided <paramref name="address"/> URI.
    /// </summary>
    public static IServiceCollection AddCustodexClient(this IServiceCollection services, Uri address)
    {
        services.AddSingleton(_ => GrpcChannel.ForAddress(address));
        services.AddSingleton(sp => new Proto.Decision.DecisionClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(sp => new Proto.Relations.RelationsClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(sp => new Proto.Schema.SchemaClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(sp => new Proto.Provisioning.ProvisioningClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(sp => new Proto.Metrics.MetricsClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton<IAuthorizer>(sp =>
            new GrpcAuthorizer(sp.GetRequiredService<Proto.Decision.DecisionClient>()));
        services.AddSingleton<IMetricsSnapshotProvider>(sp =>
            new GrpcMetricsSnapshotProvider(sp.GetRequiredService<Proto.Metrics.MetricsClient>()));
        services.AddSingleton<IRelationManager>(sp =>
            new GrpcRelationManager(sp.GetRequiredService<Proto.Relations.RelationsClient>()));
        services.AddSingleton<ISchemaManager>(sp =>
            new GrpcSchemaManager(sp.GetRequiredService<Proto.Schema.SchemaClient>()));
        services.AddSingleton<IStoreManager>(sp =>
            new GrpcStoreManager(sp.GetRequiredService<Proto.Provisioning.ProvisioningClient>()));
        services.AddSingleton<ITenantManager>(sp =>
            new GrpcTenantManager(sp.GetRequiredService<Proto.Provisioning.ProvisioningClient>()));
        return services;
    }
}
