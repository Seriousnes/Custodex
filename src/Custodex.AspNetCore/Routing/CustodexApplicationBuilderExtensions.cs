using Microsoft.AspNetCore.Builder;

namespace Custodex.AspNetCore;

/// <summary>
/// Endpoint and middleware wiring for the Custodex service surface.
/// </summary>
public static class CustodexApplicationBuilderExtensions
{
    /// <summary>
    /// Maps the Custodex gRPC services and the REST API onto the application. Each gRPC service is
    /// guarded by its policy: the decision service requires <c>Custodex:decide</c>; the relations,
    /// schema, and provisioning services require <c>Custodex:manage</c>. The REST endpoints carry the
    /// same policies per group. Requires <see cref="CustodexServiceCollectionExtensions.AddCustodexService"/>
    /// and the authentication/authorization middleware to be in the pipeline.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application, for chaining.</returns>
    public static WebApplication MapCustodex(this WebApplication app)
    {
        app.MapGrpcService<DecisionGrpcService>().RequireAuthorization(CustodexServiceCollectionExtensions.DecidePolicy);
        app.MapGrpcService<RelationsGrpcService>().RequireAuthorization(CustodexServiceCollectionExtensions.ManagePolicy);
        app.MapGrpcService<SchemaGrpcService>().RequireAuthorization(CustodexServiceCollectionExtensions.ManagePolicy);
        app.MapGrpcService<ProvisioningGrpcService>().RequireAuthorization(CustodexServiceCollectionExtensions.ManagePolicy);
        app.MapCustodexRest();
        return app;
    }

    /// <summary>
    /// Adds the tenant-resolution middleware, which derives the request's store and tenant from the
    /// authenticated principal's claims and the <c>X-Custodex-Tenant</c> header. Place this after the
    /// authentication and authorization middleware so the principal is available.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The web application, for chaining.</returns>
    public static WebApplication UseCustodexTenantResolution(this WebApplication app)
    {
        app.UseMiddleware<TenantResolutionMiddleware>();
        return app;
    }
}
