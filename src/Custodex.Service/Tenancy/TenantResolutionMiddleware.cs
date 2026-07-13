using System.Security.Claims;

namespace Custodex.Service.Tenancy;

internal sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    internal const string TenantHeader = "X-Custodex-Tenant";
    private const string StoreClaim = "Custodex:store";
    private const string TenantClaim = "Custodex:tenant";

    public async Task InvokeAsync(HttpContext context, TenantContextAccessor accessor)
    {
        var store = context.User.FindFirstValue(StoreClaim);
        if (!string.IsNullOrEmpty(store))
        {
            accessor.Store = store;

            var entitled = context.User.FindAll(TenantClaim).Select(c => c.Value).ToList();

            string? tenant = null;
            if (context.Request.Headers.TryGetValue(TenantHeader, out var tenantValues))
            {
                var requested = tenantValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(requested) && entitled.Contains(requested, StringComparer.Ordinal))
                    tenant = requested;
            }

            tenant ??= entitled.Count > 0 ? entitled[0] : null;

            if (!string.IsNullOrEmpty(tenant))
                accessor.Value = new Abstractions.TenantContext(store, tenant);
        }

        await next(context);
    }
}
