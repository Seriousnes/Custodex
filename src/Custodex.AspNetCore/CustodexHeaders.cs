namespace Custodex.AspNetCore;

/// <summary>The request headers the default Custodex resolvers read.</summary>
public static class CustodexHeaders
{
    /// <summary>The header carrying the Custodex tenant id (<c>X-Custodex-Tenant</c>).</summary>
    public const string Tenant = "X-Custodex-Tenant";
}
