using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Service.Tests.Tenancy;

[Collection("service")]
public sealed class TenantResolutionTests(PostgresFixture pg)
{
    private const string ReaderKey = "res-key";
    private const string ReaderStore = "res-store";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:ApiKeys:0:Key", ReaderKey);
            b.UseSetting("Custodex:ApiKeys:0:Store", ReaderStore);
            b.UseSetting("Custodex:ApiKeys:0:Role", "reader");
        });

    [Fact]
    public async Task Check_with_reader_key_but_no_tenant_header_returns_400()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", ReaderKey);

        var resp = await client.PostAsJsonAsync("/api/check", new
        {
            @object = new { type = "res", id = "1" },
            permission = "view",
            context = new { subject = new { type = "user", id = "alice" }, attributes = new { } },
        });

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Check_with_reader_key_and_tenant_header_resolves_context()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var storeMgr = scope.ServiceProvider.GetRequiredService<Custodex.Abstractions.IStoreManager>();
        var tenantMgr = scope.ServiceProvider.GetRequiredService<Custodex.Abstractions.ITenantManager>();
        await storeMgr.CreateStoreAsync(ReaderStore);
        await tenantMgr.CreateTenantAsync(new Custodex.Abstractions.TenantContext(ReaderStore, tenantId));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", ReaderKey);
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenantId);

        var resp = await client.PostAsJsonAsync("/api/check", new
        {
            @object = new { type = "res", id = "1" },
            permission = "view",
            context = new { subject = new { type = "user", id = "alice" }, attributes = new { } },
        });

        resp.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }
}
