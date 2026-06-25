namespace Custodex.Service.Rest;

/// <summary>
/// Extension methods that wire the Custodex REST API onto a <see cref="WebApplication"/>,
/// mapping the decision and management endpoint groups onto the application.
/// </summary>
public static partial class RestEndpoints
{
    /// <summary>
    /// Maps the Custodex REST API onto the application, rooted at <c>/v1</c>.
    /// </summary>
    public static WebApplication MapCustodexRest(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        var decision = v1.MapGroup(string.Empty).WithTags("decision").RequireAuthorization("Custodex:decide");
        MapDecisionEndpoints(decision);

        var management = v1.MapGroup(string.Empty).WithTags("management").RequireAuthorization("Custodex:manage");
        MapManagementEndpoints(management);

        return app;
    }

    static partial void MapDecisionEndpoints(RouteGroupBuilder group);
    static partial void MapManagementEndpoints(RouteGroupBuilder group);
}
