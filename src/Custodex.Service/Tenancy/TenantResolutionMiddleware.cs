using System.Security.Claims;

namespace Custodex.Service.Tenancy;

internal sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    internal const string TenantHeader = "X-Custodex-Tenant";

    public async Task InvokeAsync(HttpContext context, TenantContextAccessor accessor)
    {
        var store = context.User.FindFirstValue("Custodex:store");
        if (!string.IsNullOrEmpty(store))
        {
            context.Request.Headers.TryGetValue(TenantHeader, out var tenantValues);
            var tenant = tenantValues.FirstOrDefault()
                ?? context.User.FindFirstValue("Custodex:tenant");

            if (!string.IsNullOrEmpty(tenant))
                accessor.Value = new Abstractions.TenantContext(store, tenant);
        }

        await next(context);
    }
}
