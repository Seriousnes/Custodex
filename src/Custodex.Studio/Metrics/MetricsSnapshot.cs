namespace Custodex.Studio.Metrics;

/// <summary>
/// A point-in-time view of the engine's process-wide instruments. Latency percentiles
/// (<paramref name="CheckP50Ms"/>, <paramref name="CheckP95Ms"/>, <paramref name="CheckP99Ms"/>)
/// and <paramref name="CheckCount"/> are computed over the aggregator's rolling window; the cache
/// counters (<paramref name="CacheHits"/>, <paramref name="CacheMisses"/>, <paramref name="CacheSwept"/>)
/// are cumulative totals observed since the aggregator started. Percentiles and counts are zero when
/// the window is empty.
/// </summary>
/// <param name="CapturedAt">When this snapshot was taken, from the aggregator's time source.</param>
/// <param name="CheckCount">Number of Check latency samples retained in the rolling window.</param>
/// <param name="CheckP50Ms">The 50th-percentile Check latency over the window, in milliseconds.</param>
/// <param name="CheckP95Ms">The 95th-percentile Check latency over the window, in milliseconds.</param>
/// <param name="CheckP99Ms">The 99th-percentile Check latency over the window, in milliseconds.</param>
/// <param name="CacheHits">Cumulative decision-cache hits observed since start.</param>
/// <param name="CacheMisses">Cumulative decision-cache misses observed since start.</param>
/// <param name="CacheSwept">Cumulative cache entries swept by epoch invalidation since start.</param>
public sealed record MetricsSnapshot(
    DateTimeOffset CapturedAt,
    long CheckCount,
    double CheckP50Ms,
    double CheckP95Ms,
    double CheckP99Ms,
    long CacheHits,
    long CacheMisses,
    long CacheSwept);
