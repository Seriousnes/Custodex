namespace Custodex.Studio.Metrics;

/// <summary>
/// Supplies a thread-safe, point-in-time <see cref="MetricsSnapshot"/> of the engine's in-process
/// instruments. The console injects this to render its metrics dashboard without any out-of-process call.
/// </summary>
public interface IMetricsSnapshotProvider
{
    /// <summary>Captures the current metrics as a snapshot. Synchronous and safe to call concurrently.</summary>
    /// <returns>A snapshot reflecting the window and cumulative counters at the moment of the call.</returns>
    MetricsSnapshot Capture();
}
