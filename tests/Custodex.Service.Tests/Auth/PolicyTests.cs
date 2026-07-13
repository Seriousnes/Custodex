using System.Net;
using System.Net.Http.Json;

using Custodex.Service.Rest;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Service.Tests.Auth;

[Collection("service")]
public sealed class PolicyTests(PostgresFixture pg)
{
    private const string ReaderKey = "pol-reader-key";
    private const string AdminKey = "pol-admin-key";
    private const string Store = "pol-store";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:ApiKeys:0:Key", ReaderKey);
            b.UseSetting("Custodex:ApiKeys:0:Store", Store);
            b.UseSetting("Custodex:ApiKeys:0:Role", "reader");
            b.UseSetting("Custodex:ApiKeys:1:Key", AdminKey);
            b.UseSetting("Custodex:ApiKeys:1:Store", Store);
            b.UseSetting("Custodex:ApiKeys:1:Role", "admin");
            b.UseSetting("Custodex:ApiKeys:1:Tenants:0", "tnt-admin");
        });

    [Fact]
    public async Task WriteTuples_with_reader_key_returns_403()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", ReaderKey);
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", "any-tenant");

        var resp = await client.PostAsJsonAsync("/api/tuples",
            new WriteTuplesRequestDto(Store, "any-tenant", "test", []));

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WriteTuples_with_admin_key_does_not_return_403()
    {
        await using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var storeMgr = scope.ServiceProvider.GetRequiredService<Custodex.Abstractions.IStoreManager>();
        var tenantMgr = scope.ServiceProvider.GetRequiredService<Custodex.Abstractions.ITenantManager>();
        await storeMgr.CreateStoreAsync(Store);
        await tenantMgr.CreateTenantAsync(new Custodex.Abstractions.TenantContext(Store, "tnt-admin"));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", AdminKey);
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", "tnt-admin");

        var resp = await client.PostAsJsonAsync("/api/tuples",
            new WriteTuplesRequestDto(Store, "tnt-admin", "test", []));

        resp.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        resp.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }
}
