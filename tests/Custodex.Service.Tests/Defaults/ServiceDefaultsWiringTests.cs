using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Custodex.Service.Tests.Defaults;

[Collection("service")]
public sealed class ServiceDefaultsWiringTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
        });

    [Fact]
    public async Task Health_endpoint_includes_postgres_check()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("postgres");
    }

    [Fact]
    public async Task Alive_endpoint_returns_ok()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/alive");

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
