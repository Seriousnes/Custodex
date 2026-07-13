using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class StudioMountTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory(bool withAdminApiKey = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            if (withAdminApiKey)
                b.UseAdminApiKey();
        });

    [Fact]
    public async Task Anonymous_console_request_is_denied()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/studio");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_admin_request_serves_the_blazor_shell_in_process()
    {
        await using var factory = CreateFactory(withAdminApiKey: true);
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/studio");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("blazor.web.js");
        body.ShouldContain("Operator console");
    }
}
