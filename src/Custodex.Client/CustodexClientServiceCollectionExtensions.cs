using Custodex.Abstractions;
using Custodex.Client.Transport;

using Grpc.Core;
using Grpc.Core.Interceptors;
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
    /// interfaces. Swap in-process evaluation for the remote service by replacing the
    /// <c>AddCustodex().UsePostgres</c> call with this one; all other code is unchanged.
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
        return services.AddCustodexClientFacades();
    }

    /// <summary>
    /// Registers the Custodex gRPC client facades and attaches <paramref name="apiKey"/> as the
    /// <c>X-Custodex-Key</c> credential on every call. Use an operator key (one carrying
    /// <c>Custodex:allowAllStores</c>) to reach any store; the target store travels per call in the
    /// tenant metadata the facades already send.
    /// </summary>
    public static IServiceCollection AddCustodexClient(this IServiceCollection services, string address, string apiKey) =>
        services.AddCustodexClient(new Uri(address), apiKey);

    /// <summary>Registers the credentialed client facades using the provided <paramref name="address"/> URI.</summary>
    public static IServiceCollection AddCustodexClient(this IServiceCollection services, Uri address, string apiKey)
    {
        services.AddSingleton(_ => GrpcChannel.ForAddress(address));
        services.AddSingleton<CallInvoker>(sp =>
            sp.GetRequiredService<GrpcChannel>().Intercept(new ApiKeyInterceptor(apiKey)));
        services.AddSingleton(sp => new Proto.Decision.DecisionClient(sp.GetRequiredService<CallInvoker>()));
        services.AddSingleton(sp => new Proto.Relations.RelationsClient(sp.GetRequiredService<CallInvoker>()));
        services.AddSingleton(sp => new Proto.Schema.SchemaClient(sp.GetRequiredService<CallInvoker>()));
        services.AddSingleton(sp => new Proto.Provisioning.ProvisioningClient(sp.GetRequiredService<CallInvoker>()));
        services.AddSingleton(sp => new Proto.Metrics.MetricsClient(sp.GetRequiredService<CallInvoker>()));
        return services.AddCustodexClientFacades();
    }

    private static IServiceCollection AddCustodexClientFacades(this IServiceCollection services)
    {
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
