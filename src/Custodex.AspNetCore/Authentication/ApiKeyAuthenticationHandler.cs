using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

/// <summary>
/// Validates the <c>X-Custodex-Key</c> request header against the configured key map,
/// emitting <c>Custodex:store</c>, <c>Custodex:role</c>, <c>NameIdentifier</c>, and one
/// <c>Custodex:tenant</c> claim per configured tenant on success, plus <c>Custodex:allowAllStores</c>
/// when the matched key carries the operator capability. The tenant claims bound the tenants the key
/// may act as, so a client-supplied <c>X-Custodex-Tenant</c> header can only select a tenant the key
/// is entitled to.
/// Returns <see cref="AuthenticateResult.NoResult"/> when the header is absent so other schemes may run;
/// returns <see cref="AuthenticateResult.Fail(string)"/> for an unrecognized key value.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Options.HeaderName, out var headerValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presentedValue = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(presentedValue))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(presentedValue));
        ApiKeyEntry? entry = null;
        foreach (var candidate in Options.Keys)
        {
            var stored = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Key));
            if (CryptographicOperations.FixedTimeEquals(presented, stored))
                entry = candidate;
        }

        if (entry is null)
        {
            var fingerprint = Convert.ToHexString(presented.AsSpan(0, 4));
            Logger.LogWarning(
                "API key authentication failed for fingerprint {KeyFingerprint} from {RemoteIp}.",
                fingerprint,
                Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Unrecognized API key."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"apikey:{entry.Store}"),
            new("Custodex:store", entry.Store),
            new("Custodex:role", entry.Role),
        };
        if (entry.AllowAllStores)
            claims.Add(new Claim("Custodex:allowAllStores", "true"));
        foreach (var tenant in entry.Tenants)
            claims.Add(new Claim("Custodex:tenant", tenant));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
