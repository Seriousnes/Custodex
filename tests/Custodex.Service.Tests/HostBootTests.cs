using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Custodex.Abstractions;
using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class HostBootTests(PostgresFixture pg) : IClassFixture<WebApplicationFactory<Program>>
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
        });

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task IAuthorizer_resolves_from_host_scope()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var authorizer = scope.ServiceProvider.GetService<IAuthorizer>();

        authorizer.ShouldNotBeNull();
    }
}
