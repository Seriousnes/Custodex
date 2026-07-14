using Microsoft.AspNetCore.OpenApi;

namespace Custodex.AspNetCore;

/// <summary>
/// OpenAPI configuration for the Custodex REST surface.
/// </summary>
public static class CustodexOpenApiExtensions
{
    /// <summary>
    /// Titles the OpenAPI document and declares the <c>ApiKey</c> and <c>Bearer</c> security schemes,
    /// applying the security requirement to every operation that requires authorization. Call from
    /// within <c>AddOpenApi</c>.
    /// </summary>
    /// <param name="options">The OpenAPI options to configure.</param>
    /// <returns>The options, for chaining.</returns>
    public static OpenApiOptions AddCustodexApiDocumentation(this OpenApiOptions options)
    {
        options.AddDocumentTransformer<CustodexOpenApiDocumentTransformer>();
        options.AddOperationTransformer<CustodexOpenApiOperationTransformer>();
        return options;
    }
}
