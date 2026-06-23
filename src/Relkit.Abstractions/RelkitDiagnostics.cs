using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Relkit.Abstractions;

public static class RelkitDiagnostics
{
    public const string Name = "Relkit";
    public static readonly ActivitySource ActivitySource = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> CheckDuration =
        Meter.CreateHistogram<double>("relkit.check.duration", unit: "ms");
    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("relkit.cache.hits");
    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("relkit.cache.misses");
}
