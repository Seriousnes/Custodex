namespace Custodex.AspNetCore;

/// <summary>The request headers the default Custodex resolvers read.</summary>
public static class CustodexHeaders
{
    /// <summary>The header carrying the Custodex tenant id (<c>X-Custodex-Tenant</c>).</summary>
    public const string Tenant = "X-Custodex-Tenant";

    /// <summary>The header an operator credential uses to select the target store per call (<c>X-Custodex-Store</c>).</summary>
    public const string Store = "X-Custodex-Store";

    /// <summary>The header carrying an API key (<c>X-Custodex-Key</c>).</summary>
    public const string ApiKey = "X-Custodex-Key";
}
