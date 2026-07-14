using Custodex.Abstractions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Custodex.AspNetCore;

/// <summary>
/// Extension method that maps Custodex engine exceptions to RFC 9457 problem details responses.
/// </summary>
public static partial class RestEndpoints
{
    /// <summary>
    /// Registers an exception handler that converts engine exceptions to HTTP problem details.
    /// Caller-bug exceptions (unknown type/relation/permission, schema validation) map to 400;
    /// evaluation limit and exclusion cycle to 422; all others to 500.
    /// </summary>
    public static WebApplication UseCustodexProblemDetails(this WebApplication app)
    {
        app.UseExceptionHandler(handler =>
        {
            handler.Run(async context =>
            {
                var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                if (feature is null) return;

                var ex = feature.Error;
                var (status, title) = ex switch
                {
                    MissingTenantContextException => (400, "Missing tenant context."),
                    UnknownTypeException => (400, "Unknown entity type."),
                    UnknownRelationException => (400, "Unknown relation."),
                    UnknownPermissionException => (400, "Unknown permission."),
                    SchemaValidationException => (400, "Schema validation failed."),
                    InvalidConsistencyTokenException => (400, "Invalid consistency token."),
                    System.Text.Json.JsonException => (400, "Malformed JSON in request body."),
                    EvaluationLimitException => (422, "Evaluation limit exceeded."),
                    ExclusionCycleException => (422, "Permission cycle through an exclusion."),
                    _ => (500, "An unexpected error occurred."),
                };

                if (status == 500)
                {
                    context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Custodex.Service.Rest.ProblemDetails")
                        .LogError(ex, "Unhandled exception in the REST request pipeline.");
                }

                context.Response.StatusCode = status;
                context.Response.ContentType = "application/problem+json";

                var problem = new
                {
                    type = $"https://custodex.dev/errors/{status}",
                    title,
                    status,
                    detail = status == 500 ? title : ex.Message,
                };

                await context.Response.WriteAsJsonAsync(problem);
            });
        });

        return app;
    }
}
