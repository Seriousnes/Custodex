using System.Net.Http.Json;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class DecisionRestTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory(string tenant) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenant);
        });

    private static HttpClient CreateTenantClient(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenant);
        return client;
    }

    [Fact]
    public async Task Check_returns_allowed_true_for_granted_subject()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenantId);
        var objId = await SetupMinimalSchema(factory, tenantId);

        var client = CreateTenantClient(factory, tenantId);
        var req = new CheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            Object: new EntityRefDto("widget", objId),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null));

        var resp = await client.PostAsJsonAsync("/api/check", req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();

        result!.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_returns_allowed_false_for_ungranted_subject()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenantId);
        var objId = await SetupMinimalSchema(factory, tenantId);

        var client = CreateTenantClient(factory, tenantId);
        var req = new CheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            Object: new EntityRefDto("widget", objId),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "other", null),
                Now: null,
                Attributes: null));

        var resp = await client.PostAsJsonAsync("/api/check", req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();

        result!.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Check_with_explain_true_returns_explain_node()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenantId);
        var objId = await SetupMinimalSchema(factory, tenantId);

        var client = CreateTenantClient(factory, tenantId);
        var req = new CheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            Object: new EntityRefDto("widget", objId),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null),
            Explain: true);

        var resp = await client.PostAsJsonAsync("/api/check", req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();

        result!.Allowed.ShouldBeTrue();
        result.Explain.ShouldNotBeNull();
    }

    [Fact]
    public async Task ListObjects_returns_granted_object_ids()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenantId);
        var objId = await SetupMinimalSchema(factory, tenantId);

        var client = CreateTenantClient(factory, tenantId);
        var req = new ListObjectsRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            ObjectType: "widget",
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null));

        var resp = await client.PostAsJsonAsync("/api/list-objects", req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ListObjectsResponseDto>();

        result!.ObjectIds.ShouldContain(objId);
    }

    [Fact]
    public async Task BatchCheck_returns_aligned_true_false_results()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenantId);
        var objId = await SetupMinimalSchema(factory, tenantId);

        var client = CreateTenantClient(factory, tenantId);
        var ctx = new RequestContextDto(
            Subject: new SubjectRefDto("user", "u-1", null),
            Now: null,
            Attributes: null);
        var req = new BatchCheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            Checks: [
                new CheckItemDto(new EntityRefDto("widget", objId), "view"),
                new CheckItemDto(new EntityRefDto("widget", "no-such"), "view"),
            ],
            Context: ctx);

        var resp = await client.PostAsJsonAsync("/api/batch-check", req);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<BatchCheckResponseDto>();

        result!.Results.Count.ShouldBe(2);
        result.Results[0].Allowed.ShouldBeTrue();
        result.Results[1].Allowed.ShouldBeFalse();
    }

    private static async Task<string>
        SetupMinimalSchema(WebApplicationFactory<Program> factory, string tenant)
    {
        var objId = $"obj-{Guid.NewGuid():N}";
        var tc = new TenantContext(TestAuthHelper.AdminStore, tenant);

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

        await storeMgr.CreateStoreAsync(TestAuthHelper.AdminStore);
        await tenantMgr.CreateTenantAsync(tc);
        await schemaMgr.SetActiveSchemaAsync(TestAuthHelper.AdminStore, schema);
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", objId), "owner", new SubjectRef("user", "u-1")),
        ]);

        return objId;
    }
}
