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

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:Jwt:SigningKey", System.Convert.ToBase64String(SigningKeyBytes));
            b.UseSetting("Custodex:Jwt:Issuer", "test-issuer");
            b.UseSetting("Custodex:Jwt:Audience", "test-audience");
            b.UseSetting("Custodex:Jwt:StoreClaim", "Custodex:store");
            b.UseSetting("Custodex:Jwt:RoleClaim", "Custodex:role");
        });

    private static string CreateToken(string store, string role, string storeClaimType = "Custodex:store")
    {
        var key = new SymmetricSecurityKey(SigningKeyBytes);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(storeClaimType, store),
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
        await using var factory = CreateFactory();
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
            b.UseSetting("Custodex:Jwt:Issuer", "test-issuer");
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

    [Fact]
    public async Task Check_with_wrong_issuer_when_issuer_is_configured_returns_401()
    {
        var store = $"s-{Guid.NewGuid():N}";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:Jwt:SigningKey", System.Convert.ToBase64String(SigningKeyBytes));
            b.UseSetting("Custodex:Jwt:Issuer", "custodex-expected-issuer");
            b.UseSetting("Custodex:Jwt:Audience", "test-audience");
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

    [Fact]
    public async Task Bearer_token_carrying_the_store_under_a_custom_claim_can_act_on_that_store()
    {
        var store = $"s-{Guid.NewGuid():N}";
        const string storeClaim = "https://custodex.example/claims/store";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:Jwt:SigningKey", System.Convert.ToBase64String(SigningKeyBytes));
            b.UseSetting("Custodex:Jwt:Issuer", "test-issuer");
            b.UseSetting("Custodex:Jwt:Audience", "test-audience");
            b.UseSetting("Custodex:Jwt:StoreClaim", storeClaim);
        });
        var client = factory.CreateClient();
        var token = CreateToken(store, "admin", storeClaim);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/stores", new { store });

        resp.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public void Building_host_with_signing_key_and_no_issuer_or_audience_throws()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:Jwt:SigningKey", System.Convert.ToBase64String(SigningKeyBytes));
        });

        Should.Throw<InvalidOperationException>(() => factory.Server);
    }
}
