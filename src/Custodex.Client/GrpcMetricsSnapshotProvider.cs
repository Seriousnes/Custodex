using Custodex.Abstractions;
using Custodex.Client.Transport;
using Custodex.Protos;

using Proto = Custodex.Api;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="IMetricsSnapshotProvider"/> by fetching the live engine's snapshot from a
/// remote <c>Custodex.Service</c> over gRPC via a non-blocking asynchronous unary call; typed engine
/// exceptions are preserved via <see cref="RemoteStatus"/>.
/// </summary>
public sealed class GrpcMetricsSnapshotProvider(Proto.Metrics.MetricsClient client) : IMetricsSnapshotProvider
{
    /// <inheritdoc/>
    public async Task<MetricsSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.GetSnapshotAsync(new Proto.GetMetricsSnapshotRequest(), cancellationToken: ct).ResponseAsync);
        return ProtoMap.FromProto(response);
    }
}
