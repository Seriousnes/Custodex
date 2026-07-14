namespace Custodex.AspNetCore;

/// <summary>
/// Thrown when a handler requires a resolved <see cref="Custodex.Abstractions.TenantContext"/>
/// but the request carried no store/tenant context. Maps to HTTP 400 / gRPC InvalidArgument.
/// </summary>
public sealed class MissingTenantContextException()
    : Exception("Request requires a resolved tenant context. Supply the 'X-Custodex-Tenant' header.");
