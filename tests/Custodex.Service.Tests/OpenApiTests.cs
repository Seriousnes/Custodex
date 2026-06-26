using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class OpenApiTests(PostgresFixture pg)
{
    [Fact]
    public async Task Swagger_json_endpoint_returns_200_with_api_title()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
        });

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/swagger/api/swagger.json");

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("Custodex Authorization API");
    }
}
