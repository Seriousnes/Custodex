using Custodex.Abstractions;

namespace Custodex.Service.Rest;

/// <summary>
/// Extension method that maps Custodex engine exceptions to RFC 9457 problem details responses.
/// </summary>
public static partial class RestEndpoints
{
    /// <summary>
    /// Registers an exception handler that converts engine exceptions to HTTP problem details.
    /// Caller-bug exceptions (unknown type/relation/permission, schema validation) map to 400;
    /// evaluation limit to 422; all others to 500.
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
                    UnknownTypeException => (400, "Unknown entity type."),
                    UnknownRelationException => (400, "Unknown relation."),
                    UnknownPermissionException => (400, "Unknown permission."),
                    SchemaValidationException => (400, "Schema validation failed."),
                    EvaluationLimitException => (422, "Evaluation limit exceeded."),
                    _ => (500, "An unexpected error occurred."),
                };

                context.Response.StatusCode = status;
                context.Response.ContentType = "application/problem+json";

                var problem = new
                {
                    type = $"https://custodex.dev/errors/{status}",
                    title,
                    status,
                    detail = ex.Message,
                };

                await context.Response.WriteAsJsonAsync(problem);
            });
        });

        return app;
    }
}
