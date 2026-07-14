using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class SecurityHeadersTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory(string environment = "Development") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment(environment);
        });

    [Fact]
    public async Task Response_carries_security_headers()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");
        response.Headers.GetValues("Content-Security-Policy").First().ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Error_response_carries_security_headers()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
        });
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/check", new
        {
            @object = new { type = "res", id = "1" },
            permission = "view",
            context = new { subject = new { type = "user", id = "u-1" }, attributes = new { } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("Content-Security-Policy").First().ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Https_response_outside_development_carries_hsts_header()
    {
        await using var factory = CreateFactory("Production");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://custodex.test"),
        });
        client.DefaultRequestHeaders.Host = "custodex.test";

        var response = await client.GetAsync("/__no_such_route__");

        response.Headers.TryGetValues("Strict-Transport-Security", out _).ShouldBeTrue();
    }
}
