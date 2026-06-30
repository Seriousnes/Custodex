using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

/// <summary>Resolves the store and tenant a decision is evaluated within.</summary>
public interface ICustodexTenantResolver
{
    /// <summary>Attempts to resolve the tenant scope for the current request.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="httpContext">The ambient request, or <see langword="null"/> outside an HTTP request.</param>
    /// <param name="tenant">The resolved store and tenant when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a tenant was resolved; otherwise <see langword="false"/>, which denies the request.</returns>
    bool TryResolve(ClaimsPrincipal user, HttpContext? httpContext, out TenantContext tenant);
}
