using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Custodex.AspNetCore;

/// <summary>
/// Extension methods that wire the Custodex REST API onto a <see cref="WebApplication"/>,
/// mapping the decision, metrics, and management endpoint groups onto the application.
/// </summary>
public static partial class RestEndpoints
{
    /// <summary>
    /// Maps the Custodex REST API onto the application, rooted at <c>/api</c>.
    /// </summary>
    public static WebApplication MapCustodexRest(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        var decision = api.MapGroup(string.Empty).WithTags("decision")
            .RequireAuthorization(CustodexServiceCollectionExtensions.DecidePolicy);
        MapDecisionEndpoints(decision);

        var metrics = api.MapGroup(string.Empty).WithTags("metrics")
            .RequireAuthorization(CustodexServiceCollectionExtensions.DecidePolicy);
        MapMetricsEndpoints(metrics);

        var management = api.MapGroup(string.Empty).WithTags("management")
            .RequireAuthorization(CustodexServiceCollectionExtensions.ManagePolicy);
        MapManagementEndpoints(management);

        return app;
    }

    static partial void MapDecisionEndpoints(RouteGroupBuilder group);
    static partial void MapMetricsEndpoints(RouteGroupBuilder group);
    static partial void MapManagementEndpoints(RouteGroupBuilder group);
}
