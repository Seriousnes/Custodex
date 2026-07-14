using Custodex.Abstractions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Custodex.AspNetCore;

public static partial class RestEndpoints
{
    static partial void MapMetricsEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/metrics", async (IMetricsSnapshotProvider snapshots, CancellationToken ct) =>
            Results.Ok(RestMap.ToDto(await snapshots.CaptureAsync(ct))))
        .WithName("GetMetricsSnapshot")
        .WithSummary("Capture a point-in-time snapshot of the engine's process-wide metrics.");
    }
}
