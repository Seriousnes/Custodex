using Bunit;

using Custodex.Abstractions;

using Custodex.Studio;
using Custodex.Studio.Components.Pages;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Services;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class MetricsDashboardTests
{
    private sealed class FakeMetricsSnapshotProvider : IMetricsSnapshotProvider
    {
        private readonly MetricsSnapshot _snapshot;

        public FakeMetricsSnapshotProvider(MetricsSnapshot snapshot) => _snapshot = snapshot;

        public Task<MetricsSnapshot> CaptureAsync(CancellationToken ct = default) => Task.FromResult(_snapshot);
    }

    private static BunitContext CreateContext(IMetricsSnapshotProvider provider)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(new StudioConnectionState());
        ctx.Services.AddSingleton(provider);
        return ctx;
    }

    [Fact]
    public void Renders_initial_snapshot_cache_counters_and_a_latency_chart()
    {
        var snapshot = new MetricsSnapshot(DateTimeOffset.UnixEpoch, 7, 1.5, 2.5, 3.5, 11, 22, 33);
        using var ctx = CreateContext(new FakeMetricsSnapshotProvider(snapshot));

        var cut = ctx.Render<MetricsDashboard>();

        cut.Find("#cache-hits").TextContent.ShouldContain("11");
        cut.Find("#cache-misses").TextContent.ShouldContain("22");
        cut.Find("#cache-swept").TextContent.ShouldContain("33");
        cut.Find("#check-count").TextContent.ShouldContain("7");
        cut.FindComponents<MudChart<double>>().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Renders_zero_snapshot_without_error()
    {
        var snapshot = new MetricsSnapshot(DateTimeOffset.UnixEpoch, 0, 0, 0, 0, 0, 0, 0);
        using var ctx = CreateContext(new FakeMetricsSnapshotProvider(snapshot));

        var cut = ctx.Render<MetricsDashboard>();

        cut.Find("#cache-hits").TextContent.ShouldContain("0");
        cut.FindComponents<MudChart<double>>().Count.ShouldBeGreaterThan(0);
    }
}
