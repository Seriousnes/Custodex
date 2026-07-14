using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

internal sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    internal const string TenantHeader = "X-Custodex-Tenant";
    internal const string StoreHeader = "X-Custodex-Store";
    private const string StoreClaim = "Custodex:store";
    private const string TenantClaim = "Custodex:tenant";
    private const string AllowAllStoresClaim = "Custodex:allowAllStores";
    private const string Wildcard = "*";

    public async Task InvokeAsync(HttpContext context, TenantContextAccessor accessor)
    {
        var claimStore = context.User.FindFirstValue(StoreClaim);
        var isOperator = string.Equals(
            context.User.FindFirstValue(AllowAllStoresClaim), "true", StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(claimStore) || isOperator)
        {
            accessor.Operator = isOperator;
            accessor.Store = string.IsNullOrEmpty(claimStore) ? null : claimStore;

            var store = claimStore;
            if (isOperator
                && context.Request.Headers.TryGetValue(StoreHeader, out var storeValues)
                && storeValues.FirstOrDefault() is { Length: > 0 } headerStore)
            {
                store = headerStore;
            }

            var entitled = context.User.FindAll(TenantClaim).Select(c => c.Value).ToList();
            var spansAllTenants = entitled.Contains(Wildcard, StringComparer.Ordinal);

            string? tenant = null;
            if (context.Request.Headers.TryGetValue(TenantHeader, out var tenantValues))
            {
                var requested = tenantValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(requested) && requested != Wildcard
                    && (spansAllTenants || entitled.Contains(requested, StringComparer.Ordinal)))
                    tenant = requested;
            }

            tenant ??= entitled.FirstOrDefault(t => t != Wildcard);

            if (!string.IsNullOrEmpty(store) && !string.IsNullOrEmpty(tenant))
                accessor.Value = new Abstractions.TenantContext(store, tenant);
        }

        await next(context);
    }
}
