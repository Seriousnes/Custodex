namespace Custodex.Abstractions;

/// <summary>
/// Supplies a thread-safe, point-in-time <see cref="MetricsSnapshot"/> of the engine's instruments.
/// An in-process implementation reads the local <c>Custodex</c> meter directly; a remote implementation
/// fetches the live engine's snapshot over gRPC. Consumers such as the metrics dashboard inject this
/// interface and render identically regardless of which implementation is registered.
/// </summary>
public interface IMetricsSnapshotProvider
{
    /// <summary>Captures the current metrics as a snapshot. Synchronous and safe to call concurrently.</summary>
    /// <returns>A snapshot reflecting the window and cumulative counters at the moment of the call.</returns>
    MetricsSnapshot Capture();
}
