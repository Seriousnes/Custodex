using Custodex.Protos;
using Custodex.Api;

using Grpc.Core;

using Contracts = Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>
/// gRPC service implementation for the <c>Metrics</c> service. Returns the live engine's
/// <see cref="Contracts.MetricsSnapshot"/> from the registered <see cref="Contracts.IMetricsSnapshotProvider"/>,
/// which runs where the engine runs so it reflects real traffic.
/// </summary>
public sealed class MetricsGrpcService(Contracts.IMetricsSnapshotProvider snapshots) : Metrics.MetricsBase
{
    /// <inheritdoc />
    public override async Task<MetricsSnapshot> GetSnapshot(GetMetricsSnapshotRequest request, ServerCallContext context) =>
        ProtoMap.ToProto(await snapshots.CaptureAsync(context.CancellationToken));
}
