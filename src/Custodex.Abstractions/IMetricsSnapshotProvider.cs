namespace Custodex.Abstractions;

/// <summary>
/// Supplies a thread-safe, point-in-time <see cref="MetricsSnapshot"/> of the engine's instruments.
/// An in-process implementation reads the local <c>Custodex</c> meter directly; a remote implementation
/// fetches the live engine's snapshot over gRPC. The method is asynchronous so a remote implementation
/// never blocks the caller's thread. Consumers such as the metrics dashboard inject this interface and
/// render identically regardless of which implementation is registered.
/// </summary>
public interface IMetricsSnapshotProvider
{
    /// <summary>Captures the current metrics as a snapshot. Safe to call concurrently.</summary>
    /// <param name="ct">A token to cancel the capture.</param>
    /// <returns>A snapshot reflecting the window and cumulative counters at the moment of the call.</returns>
    Task<MetricsSnapshot> CaptureAsync(CancellationToken ct = default);
}
