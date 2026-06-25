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

    /// <summary>The role this key grants — either <c>reader</c> or <c>admin</c>.</summary>
    public string Role { get; set; } = string.Empty;
}
