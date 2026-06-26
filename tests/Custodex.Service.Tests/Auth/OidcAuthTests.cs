using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

using Shouldly;

namespace Custodex.Service.Tests.Auth;

[Collection("service")]
public sealed class OidcAuthTests(PostgresFixture pg)
{
    private static readonly byte[] SigningKeyBytes =
        System.Text.Encoding.UTF8.GetBytes("test-signing-key-must-be-at-least-32-chars");

    private WebApplicationFactory<Program> CreateFactory(string store, string role) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:Jwt:SigningKey", System.Convert.ToBase64String(SigningKeyBytes));
            b.UseSetting("Custodex:Jwt:StoreClaim", "Custodex:store");
            b.UseSetting("Custodex:Jwt:RoleClaim", "Custodex:role");
        });

    private string CreateToken(string store, string role)
    {
        var key = new SymmetricSecurityKey(SigningKeyBytes);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim("Custodex:store", store),
            new Claim("Custodex:role", role),
            new Claim(ClaimTypes.NameIdentifier, $"bearer:{store}"),
        };
        var token = new JwtSecurityToken(
            issuer: "test-issuer",
            audience: "test-audience",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Check_with_valid_bearer_token_returns_not_401()
    {
        var store = $"s-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(store, "reader");
        var client = factory.CreateClient();
        var token = CreateToken(store, "reader");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/check", new
        {
            store,
            tenant = $"t-{Guid.NewGuid():N}",
            @object = new { type = "res", id = "1" },
            permission = "view",
            context = new { subject = new { type = "user", id = "alice" }, attributes = new { } },
        });

        resp.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Check_with_wrong_audience_when_audience_is_configured_returns_401()
    {
        var store = $"s-{Guid.NewGuid():N}";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:Jwt:SigningKey", System.Convert.ToBase64String(SigningKeyBytes));
            b.UseSetting("Custodex:Jwt:Audience", "custodex-expected-audience");
        });
        var client = factory.CreateClient();
        var token = CreateToken(store, "reader");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/check", new
        {
            store,
            tenant = $"t-{Guid.NewGuid():N}",
            @object = new { type = "res", id = "1" },
            permission = "view",
            context = new { subject = new { type = "user", id = "alice" }, attributes = new { } },
        });

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
