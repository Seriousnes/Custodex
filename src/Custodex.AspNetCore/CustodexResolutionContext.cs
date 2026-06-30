using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

/// <summary>
/// The per-request inputs a Custodex resolver sees: the policy's object type and permission, the
/// authenticated principal, the authorization resource, the ambient <see cref="HttpContext"/> (when
/// one exists), and the resolved tenant.
/// </summary>
public sealed class CustodexResolutionContext
{
    internal CustodexResolutionContext(
        string objectType, string permission, ClaimsPrincipal user,
        object? resource, HttpContext? httpContext, TenantContext tenant)
    {
        ObjectType = objectType;
        Permission = permission;
        User = user;
        Resource = resource;
        HttpContext = httpContext;
        Tenant = tenant;
    }

    /// <summary>The object entity type parsed from the policy name.</summary>
    public string ObjectType { get; }

    /// <summary>The permission parsed from the policy name.</summary>
    public string Permission { get; }

    /// <summary>The authenticated principal the decision concerns.</summary>
    public ClaimsPrincipal User { get; }

    /// <summary>The authorization resource passed by the caller (for example an <c>AuthorizeView</c> resource), or <see langword="null"/>.</summary>
    public object? Resource { get; }

    /// <summary>The ambient request context, or <see langword="null"/> outside an HTTP request (for example in a Blazor circuit).</summary>
    public HttpContext? HttpContext { get; }

    /// <summary>The store and tenant the decision is evaluated within.</summary>
    public TenantContext Tenant { get; }
}
