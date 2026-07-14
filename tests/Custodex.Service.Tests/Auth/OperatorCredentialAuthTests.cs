using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Custodex.Service.Tests.Auth;

[Collection("service")]
public sealed class OperatorCredentialAuthTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory(string apiKey, string boundStore, bool allowAllStores) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:ApiKeys:0:Key", apiKey);
            b.UseSetting("Custodex:ApiKeys:0:Store", boundStore);
            b.UseSetting("Custodex:ApiKeys:0:Role", "admin");
            b.UseSetting("Custodex:ApiKeys:0:AllowAllStores", allowAllStores ? "true" : "false");
        });

    [Fact]
    public async Task Default_credential_cannot_create_a_foreign_store()
    {
        await using var factory = CreateFactory("default-key", $"bound-{Guid.NewGuid():N}", allowAllStores: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", "default-key");

        var resp = await client.PostAsJsonAsync("/api/stores", new { store = $"foreign-{Guid.NewGuid():N}" });

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Operator_credential_creates_a_foreign_store_it_is_not_bound_to()
    {
        await using var factory = CreateFactory("operator-key", $"bound-{Guid.NewGuid():N}", allowAllStores: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", "operator-key");

        var foreignStore = $"foreign-{Guid.NewGuid():N}";
        var resp = await client.PostAsJsonAsync("/api/stores", new { store = foreignStore });

        resp.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Default_credential_sending_the_store_header_is_still_rejected()
    {
        await using var factory = CreateFactory("default-key", $"bound-{Guid.NewGuid():N}", allowAllStores: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", "default-key");

        var foreignStore = $"foreign-{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Add("X-Custodex-Store", foreignStore);

        var resp = await client.PostAsJsonAsync("/api/stores", new { store = foreignStore });

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
