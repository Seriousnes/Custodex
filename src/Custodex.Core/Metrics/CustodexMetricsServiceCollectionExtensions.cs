using Custodex.Abstractions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Custodex.Core;

/// <summary>
/// Registration for the in-process metrics aggregator that listens to the engine's
/// <see cref="CustodexDiagnostics.Meter"/> and serves a pollable <see cref="MetricsSnapshot"/>.
/// </summary>
public static class CustodexMetricsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-process <see cref="IMetricsSnapshotProvider"/> backed by a meter listener that
    /// aggregates the running engine's own traffic, started and stopped with the host. Registration is
    /// idempotent, and a caller-registered <see cref="TimeProvider"/> or provider is left in place.
    /// </summary>
    /// <remarks>
    /// Check-duration measurements are only emitted by the engine registration path that applies the
    /// decision cache, so a host that registers the engine through a storage provider's <c>Use</c>
    /// chain produces them, while a hand-rolled engine registration without that chain leaves the
    /// dashboard without check-duration samples.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCustodexMetrics(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CustodexMeterAggregator>();
        services.TryAddSingleton<IMetricsSnapshotProvider>(sp => sp.GetRequiredService<CustodexMeterAggregator>());
        services.AddHostedService(sp => sp.GetRequiredService<CustodexMeterAggregator>());
        return services;
    }
}
