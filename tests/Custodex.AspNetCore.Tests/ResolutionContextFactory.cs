using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore.Tests;

internal static class ResolutionContextFactory
{
    public static CustodexResolutionContext Create(
        string objectType, string permission, object? resource, HttpContext? http, TenantContext tenant) =>
        new(objectType, permission, new ClaimsPrincipal(new ClaimsIdentity()), resource, http, tenant);
}
