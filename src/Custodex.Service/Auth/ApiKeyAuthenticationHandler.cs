using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Custodex.Service.Auth;

/// <summary>
/// Validates the <c>X-Custodex-Key</c> request header against the configured key map,
/// emitting <c>Custodex:store</c>, <c>Custodex:role</c>, and <c>NameIdentifier</c> claims on success.
/// Returns <see cref="AuthenticateResult.NoResult"/> when the header is absent so other schemes may run;
/// returns <see cref="AuthenticateResult.Fail"/> for an unrecognized key value.
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

        var rawKey = headerValues.ToString();
        var entry = Options.Keys.FirstOrDefault(k =>
            string.Equals(k.Key, rawKey, StringComparison.Ordinal));

        if (entry is null)
            return Task.FromResult(AuthenticateResult.Fail("Unrecognized API key."));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, $"apikey:{entry.Store}"),
            new Claim("Custodex:store", entry.Store),
            new Claim("Custodex:role", entry.Role),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
