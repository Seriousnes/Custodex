using Custodex.Abstractions;
using Custodex.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Shouldly;

namespace Custodex.Core.Tests.Metrics;

public sealed class AddCustodexMetricsTests
{
    [Fact]
    public void Registers_the_aggregator_as_the_snapshot_provider_and_a_hosted_service()
    {
        var services = new ServiceCollection();

        services.AddCustodexMetrics();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMetricsSnapshotProvider>().ShouldBeOfType<CustodexMeterAggregator>();
        provider.GetServices<IHostedService>().OfType<CustodexMeterAggregator>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Is_idempotent_when_called_twice()
    {
        var services = new ServiceCollection();

        services.AddCustodexMetrics();
        services.AddCustodexMetrics();

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().OfType<CustodexMeterAggregator>().Count().ShouldBe(1);
    }
}
