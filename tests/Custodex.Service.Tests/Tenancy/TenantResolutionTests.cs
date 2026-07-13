using System.Net;
using System.Net.Http.Json;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Rest;

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

    private WebApplicationFactory<Program> CreateFactory(string store, params string[] tenants) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseSetting("Custodex:ApiKeys:0:Key", ReaderKey);
            b.UseSetting("Custodex:ApiKeys:0:Store", store);
            b.UseSetting("Custodex:ApiKeys:0:Role", "reader");
            for (var i = 0; i < tenants.Length; i++)
                b.UseSetting($"Custodex:ApiKeys:0:Tenants:{i}", tenants[i]);
        });

    [Fact]
    public async Task Check_with_reader_key_but_no_tenant_header_returns_400()
    {
        await using var factory = CreateFactory(ReaderStore);
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
    public async Task Check_with_entitled_tenant_header_resolves_context()
    {
        var store = $"s-{Guid.NewGuid():N}";
        var tenantId = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(store, tenantId);
        await SeedGrantAsync(factory, store, tenantId, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", ReaderKey);
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenantId);

        var resp = await client.PostAsJsonAsync("/api/check", CheckBody());

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();
        result!.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Header_for_an_unentitled_tenant_is_not_honored()
    {
        var store = $"s-{Guid.NewGuid():N}";
        var entitled = $"a-{Guid.NewGuid():N}";
        var other = $"b-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(store, entitled);
        await SeedGrantAsync(factory, store, grantTenant: other, entitled, other);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", ReaderKey);
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", other);

        var resp = await client.PostAsJsonAsync("/api/check", CheckBody());

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();
        result!.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Header_for_a_second_entitled_tenant_is_honored()
    {
        var store = $"s-{Guid.NewGuid():N}";
        var first = $"a-{Guid.NewGuid():N}";
        var second = $"b-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(store, first, second);
        await SeedGrantAsync(factory, store, grantTenant: second, first, second);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", ReaderKey);
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", second);

        var resp = await client.PostAsJsonAsync("/api/check", CheckBody());

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();
        result!.Allowed.ShouldBeTrue();
    }

    private static object CheckBody() => new
    {
        @object = new { type = "widget", id = "1" },
        permission = "view",
        context = new { subject = new { type = "user", id = "u-1" }, attributes = new { } },
    };

    private static async Task SeedGrantAsync(
        WebApplicationFactory<Program> factory, string store, string grantTenant, params string[] tenants)
    {
        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        using var scope = factory.Services.CreateScope();
        var storeMgr = scope.ServiceProvider.GetRequiredService<IStoreManager>();
        var tenantMgr = scope.ServiceProvider.GetRequiredService<ITenantManager>();
        var schemaMgr = scope.ServiceProvider.GetRequiredService<ISchemaManager>();
        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();

        await storeMgr.CreateStoreAsync(store);
        foreach (var tenant in tenants)
            await tenantMgr.CreateTenantAsync(new TenantContext(store, tenant));
        await schemaMgr.SetActiveSchemaAsync(store, schema);
        await relMgr.WriteTuplesAsync(new TenantContext(store, grantTenant), "test",
        [
            new RelationTuple(new EntityRef("widget", "1"), "owner", new SubjectRef("user", "u-1")),
        ]);
    }
}
