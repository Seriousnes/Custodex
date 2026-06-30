using System.Security.Claims;
using System.Text.Encodings.Web;

using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore.Tests.Integration;

internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SubjectHeader = "X-Test-Subject";
    public const string StoreHeader = "X-Test-Store";
    public const string TenantHeader = "X-Test-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubjectHeader, out var subject) || subject.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject.ToString()) };
        if (Request.Headers.TryGetValue(StoreHeader, out var store))
            claims.Add(new Claim(CustodexClaimTypes.Store, store.ToString()));
        if (Request.Headers.TryGetValue(TenantHeader, out var tenant))
            claims.Add(new Claim(CustodexClaimTypes.Tenant, tenant.ToString()));

        var identity = new ClaimsIdentity(claims, "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
