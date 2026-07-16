using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.Core;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public sealed class MetricsCompositionTests
{
    [Fact]
    public void AddCustodexService_makes_the_metrics_provider_and_grpc_service_resolvable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCustodexService(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMetricsSnapshotProvider>().ShouldNotBeNull();
        var grpc = ActivatorUtilities.CreateInstance<MetricsGrpcService>(provider);
        grpc.ShouldNotBeNull();
    }
}
