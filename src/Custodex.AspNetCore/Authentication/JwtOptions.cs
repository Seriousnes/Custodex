namespace Custodex.AspNetCore;

/// <summary>
/// Configuration for the JWT bearer authentication scheme, bound from <c>Custodex:Jwt</c>.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Base-64-encoded symmetric signing key for HMAC-SHA256 token validation. When set, Authority is ignored.</summary>
    public string? SigningKey { get; set; }

    /// <summary>OIDC authority URL for JWK-set-based validation. Used when <see cref="SigningKey"/> is absent.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected token audience. When empty, audience validation is skipped.</summary>
    public string? Audience { get; set; }

    /// <summary>Claim name carrying the store identifier. Defaults to <c>Custodex:store</c>.</summary>
    public string StoreClaim { get; set; } = "Custodex:store";

    /// <summary>Claim name carrying the role identifier. Defaults to <c>Custodex:role</c>.</summary>
    public string RoleClaim { get; set; } = "Custodex:role";
}
