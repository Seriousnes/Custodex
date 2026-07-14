using Custodex.Abstractions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Custodex.AspNetCore;

public static partial class RestEndpoints
{
    static partial void MapMetricsEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/metrics", (IMetricsSnapshotProvider snapshots) =>
            Results.Ok(RestMap.ToDto(snapshots.Capture())))
        .WithName("GetMetricsSnapshot")
        .WithSummary("Capture a point-in-time snapshot of the engine's process-wide metrics.");
    }
}
