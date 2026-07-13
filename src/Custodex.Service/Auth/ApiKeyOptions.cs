using Microsoft.AspNetCore.Authentication;

namespace Custodex.Service.Auth;

/// <summary>
/// Configuration options for the <c>ApiKey</c> authentication scheme.
/// Bind from <c>Custodex:ApiKeys</c> configuration to populate <see cref="Keys"/>.
/// </summary>
public sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    /// <summary>The HTTP header name inspected for an API key. Defaults to <c>X-Custodex-Key</c>.</summary>
    public string HeaderName { get; set; } = "X-Custodex-Key";

    /// <summary>Configured API keys. Each entry maps a key value to the store it authorizes and the role it grants.</summary>
    public List<ApiKeyEntry> Keys { get; set; } = [];
}

/// <summary>A single API key with its associated store and role.</summary>
public sealed class ApiKeyEntry
{
    /// <summary>The secret key value sent in the <c>X-Custodex-Key</c> header.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The store this key authorizes access to.</summary>
    public string Store { get; set; } = string.Empty;

    /// <summary>The role this key grants - either <c>reader</c> or <c>admin</c>.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// The tenants this key may act as. Each value is emitted as a <c>Custodex:tenant</c> claim, and an
    /// <c>X-Custodex-Tenant</c> header is honored only when its value is one of them. Empty (the default)
    /// means the key carries no tenant entitlement, so data-plane requests must supply a tenant the key
    /// is entitled to. The single value <c>"*"</c> entitles the key to any concrete tenant in its store:
    /// the header may then name any tenant except the reserved <c>"*"</c> id itself.
    /// </summary>
    public List<string> Tenants { get; set; } = [];
}
