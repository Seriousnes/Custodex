using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.Service.Tests.Auth;

[Collection("service")]
public sealed class ApiKeyAuthTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory(string apiKey, string store, string role) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:ApiKeys:0:Key", apiKey);
            b.UseSetting("Custodex:ApiKeys:0:Store", store);
            b.UseSetting("Custodex:ApiKeys:0:Role", role);
        });

    [Fact]
    public async Task Check_with_no_key_returns_401()
    {
        await using var factory = CreateFactory("secret-reader-key", "s1", "reader");
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/check", new { });

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Check_with_valid_reader_key_returns_not_401()
    {
        var store = $"s-{Guid.NewGuid():N}";
        await using var factory = CreateFactory("secret-reader-key", store, "reader");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", "secret-reader-key");

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
    public async Task Check_with_unknown_key_returns_401()
    {
        await using var factory = CreateFactory("secret-reader-key", "s1", "reader");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", "wrong-key");

        var resp = await client.PostAsJsonAsync("/api/check", new { });

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Check_with_empty_key_header_returns_401_even_with_a_configured_admin_key()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
        });
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/check")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.TryAddWithoutValidation("X-Custodex-Key", "");

        var resp = await client.SendAsync(request);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Building_the_host_with_an_empty_configured_key_throws()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:ApiKeys:0:Key", "");
            b.UseSetting("Custodex:ApiKeys:0:Store", "s1");
            b.UseSetting("Custodex:ApiKeys:0:Role", "reader");
        });

        Should.Throw<OptionsValidationException>(() => factory.Server);
    }
}
