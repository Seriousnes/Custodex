namespace Custodex.AspNetCore;

/// <summary>A point-in-time view of the engine's process-wide instruments: Check-latency percentiles and
/// sample count over a rolling window, plus cumulative decision-cache counters since measurement began.</summary>
public sealed record MetricsSnapshotDto(
    DateTimeOffset CapturedAt,
    long CheckCount,
    double CheckP50Ms,
    double CheckP95Ms,
    double CheckP99Ms,
    long CacheHits,
    long CacheMisses,
    long CacheSwept);
