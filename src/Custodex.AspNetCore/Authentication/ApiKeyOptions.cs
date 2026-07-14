using Microsoft.AspNetCore.Authentication;

namespace Custodex.AspNetCore;

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
    /// Opt-in operator capability. When <see langword="true"/>, this credential is <b>not</b> bound to
    /// <see cref="Store"/>: it may act on <b>any</b> store, with the target store chosen per call from
    /// the <c>X-Custodex-Store</c> request header. Combined with a wildcard <see cref="Tenants"/>
    /// entitlement (<c>"*"</c>), it also reaches every tenant, so it grants cross-store and cross-tenant
    /// reach and defeats the per-credential store isolation that holds by default. It is intended only
    /// for development and administrative tooling. Provision such a credential only behind a gate and
    /// restrict it to a trusted network; never issue it to an untrusted caller. An operator key still
    /// needs a <see cref="Role"/> of <c>reader</c> or <c>admin</c> to clear the endpoint authorization
    /// policies. Defaults to <see langword="false"/>, which keeps the secure single-store-per-credential
    /// behavior.
    /// </summary>
    public bool AllowAllStores { get; set; }

    /// <summary>
    /// The tenants this key may act as. Each value is emitted as a <c>Custodex:tenant</c> claim, and an
    /// <c>X-Custodex-Tenant</c> header is honored only when its value is one of them. Empty (the default)
    /// means the key carries no tenant entitlement, so data-plane requests must supply a tenant the key
    /// is entitled to. The single value <c>"*"</c> entitles the key to any concrete tenant in its store:
    /// the header may then name any tenant except the reserved <c>"*"</c> id itself.
    /// </summary>
    public List<string> Tenants { get; set; } = [];
}
