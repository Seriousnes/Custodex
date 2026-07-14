using Custodex.Abstractions;
using Custodex.Client.Transport;
using Custodex.Protos;

using Proto = Custodex.Api;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="IMetricsSnapshotProvider"/> by fetching the live engine's snapshot from a
/// remote <c>Custodex.Service</c> over gRPC. Because the interface is synchronous, the blocking unary
/// call is used; typed engine exceptions are preserved via <see cref="RemoteStatus"/>.
/// </summary>
public sealed class GrpcMetricsSnapshotProvider(Proto.Metrics.MetricsClient client) : IMetricsSnapshotProvider
{
    /// <inheritdoc/>
    public MetricsSnapshot Capture()
    {
        var response = RemoteStatus.Unwrap(() => client.GetSnapshot(new Proto.GetMetricsSnapshotRequest()));
        return ProtoMap.FromProto(response);
    }
}
