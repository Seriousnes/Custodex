using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Custodex.Abstractions;

public static class CustodexDiagnostics
{
    public const string Name = "Custodex";
    public static readonly ActivitySource ActivitySource = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> CheckDuration =
        Meter.CreateHistogram<double>("Custodex.check.duration", unit: "ms");
    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("Custodex.cache.hits");
    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("Custodex.cache.misses");
    public static readonly Counter<long> CacheSwept = Meter.CreateCounter<long>("Custodex.cache.swept");
}
