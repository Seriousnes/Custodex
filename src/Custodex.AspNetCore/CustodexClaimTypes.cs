namespace Custodex.AspNetCore;

/// <summary>The claim types the default Custodex resolvers read from the authenticated principal.</summary>
public static class CustodexClaimTypes
{
    /// <summary>The claim carrying the Custodex store id (<c>Custodex:store</c>).</summary>
    public const string Store = "Custodex:store";

    /// <summary>The claim carrying the Custodex tenant id (<c>Custodex:tenant</c>), used when no tenant header is present.</summary>
    public const string Tenant = "Custodex:tenant";
}
