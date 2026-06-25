using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Custodex.Abstractions;

/// <summary>
/// The library's OpenTelemetry sources. Register the <see cref="ActivitySource"/> and <see cref="Meter"/>
/// (both named <see cref="Name"/>) with your tracer and meter providers to collect Custodex traces and metrics.
/// </summary>
public static class CustodexDiagnostics
{
    /// <summary>The shared name of the <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string Name = "Custodex";

    /// <summary>The activity source the engine's spans are emitted on.</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    /// <summary>The meter the instruments below are published on.</summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>Check evaluation latency, in milliseconds.</summary>
    public static readonly Histogram<double> CheckDuration =
        Meter.CreateHistogram<double>("Custodex.check.duration", unit: "ms");

    /// <summary>Count of decision-cache hits.</summary>
    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("Custodex.cache.hits");

    /// <summary>Count of decision-cache misses.</summary>
    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("Custodex.cache.misses");

    /// <summary>Count of cache entries dropped as stale by epoch invalidation.</summary>
    public static readonly Counter<long> CacheSwept = Meter.CreateCounter<long>("Custodex.cache.swept");
}
