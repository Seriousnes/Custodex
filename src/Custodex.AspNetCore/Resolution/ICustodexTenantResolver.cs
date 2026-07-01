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
    /// <param name="cancellationToken">A token that is cancelled when the request is aborted.</param>
    /// <returns>The resolved store and tenant, or <see langword="null"/> to deny the request.</returns>
    ValueTask<TenantContext?> ResolveAsync(ClaimsPrincipal user, HttpContext? httpContext, CancellationToken cancellationToken);
}
