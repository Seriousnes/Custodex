using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

internal sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    internal const string TenantHeader = "X-Custodex-Tenant";

    internal const string StoreHeader = "X-Custodex-Store";

    public async Task InvokeAsync(HttpContext context, TenantContextAccessor accessor)
    {
        var claimStore = context.User.FindFirstValue("Custodex:store");
        var isOperator = string.Equals(
            context.User.FindFirstValue("Custodex:allowAllStores"), "true", StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(claimStore) || isOperator)
        {
            accessor.Operator = isOperator;
            accessor.Store = string.IsNullOrEmpty(claimStore) ? null : claimStore;

            var effectiveStore = claimStore;
            if (isOperator
                && context.Request.Headers.TryGetValue(StoreHeader, out var storeValues)
                && storeValues.FirstOrDefault() is { Length: > 0 } headerStore)
            {
                effectiveStore = headerStore;
            }

            context.Request.Headers.TryGetValue(TenantHeader, out var tenantValues);
            var tenant = tenantValues.FirstOrDefault()
                ?? context.User.FindFirstValue("Custodex:tenant");

            if (!string.IsNullOrEmpty(effectiveStore) && !string.IsNullOrEmpty(tenant))
                accessor.Value = new Abstractions.TenantContext(effectiveStore, tenant);
        }

        await next(context);
    }
}
