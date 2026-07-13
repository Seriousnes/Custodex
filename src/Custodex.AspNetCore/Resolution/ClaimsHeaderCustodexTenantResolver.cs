using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class ClaimsHeaderCustodexTenantResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexTenantResolver
{
    public ValueTask<TenantContext?> ResolveAsync(ClaimsPrincipal user, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        var o = options.Value;

        var store = user.FindFirstValue(o.StoreClaim);
        if (string.IsNullOrEmpty(store))
            return ValueTask.FromResult<TenantContext?>(null);

        var entitled = user.FindAll(o.TenantClaim).Select(c => c.Value).ToList();

        string? tenantId = null;
        if (httpContext is not null && httpContext.Request.Headers.TryGetValue(o.TenantHeader, out var header))
        {
            var requested = header.ToString();
            if (!string.IsNullOrEmpty(requested) && entitled.Contains(requested, StringComparer.Ordinal))
                tenantId = requested;
        }

        tenantId ??= entitled.Count > 0 ? entitled[0] : null;
        if (string.IsNullOrEmpty(tenantId))
            return ValueTask.FromResult<TenantContext?>(null);

        return ValueTask.FromResult<TenantContext?>(new TenantContext(store, tenantId));
    }
}
