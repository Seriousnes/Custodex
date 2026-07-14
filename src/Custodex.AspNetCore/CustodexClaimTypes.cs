namespace Custodex.AspNetCore;

/// <summary>The claim types the default Custodex resolvers read from the authenticated principal.</summary>
public static class CustodexClaimTypes
{
    /// <summary>The claim carrying the Custodex store id (<c>Custodex:store</c>).</summary>
    public const string Store = "Custodex:store";

    /// <summary>The claim carrying the Custodex tenant id (<c>Custodex:tenant</c>), used when no tenant header is present.</summary>
    public const string Tenant = "Custodex:tenant";

    /// <summary>The claim carrying the role a credential is granted (<c>Custodex:role</c>), one of <c>reader</c> or <c>admin</c>.</summary>
    public const string Role = "Custodex:role";

    /// <summary>The claim marking a credential as an operator (<c>Custodex:allowAllStores</c>); present with value <c>true</c> when the credential may act on any store.</summary>
    public const string AllowAllStores = "Custodex:allowAllStores";
}
