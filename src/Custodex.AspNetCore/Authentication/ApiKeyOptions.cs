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

    /// <summary>The role this key grants — either <c>reader</c> or <c>admin</c>.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Opt-in operator capability. When <see langword="true"/>, this credential is <b>not</b> bound to
    /// <see cref="Store"/>: it may act on <b>any</b> store and <b>any</b> tenant, with the target chosen
    /// per call from the <c>x-custodex-store</c> and <c>x-custodex-tenant</c> request headers. This
    /// grants cross-store and cross-tenant reach and defeats the per-credential store isolation that
    /// holds by default, so it is intended only for development and administrative tooling. Provision
    /// such a credential only behind a gate and restrict it to a trusted network; never issue it to an
    /// untrusted caller. Note that the bundled <c>/studio</c> console is currently unauthenticated, so
    /// exposing it alongside an operator credential exposes every store and tenant. An operator key
    /// still needs a <see cref="Role"/> of <c>reader</c> or <c>admin</c> to clear the endpoint
    /// authorization policies. Defaults to <see langword="false"/>, which keeps the secure
    /// single-store-per-credential behavior.
    /// </summary>
    public bool AllowAllStores { get; set; }
}
