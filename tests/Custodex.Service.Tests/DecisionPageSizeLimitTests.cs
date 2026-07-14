using System.Net.Http.Json;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.AspNetCore;

using Grpc.Net.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Proto = Custodex.Api;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class DecisionPageSizeLimitTests(PostgresFixture pg)
{
    private const int ConfiguredMax = 2;

    private WebApplicationFactory<Program> CreateFactory(string tenant) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseSetting("Custodex:MaxPageSize", ConfiguredMax.ToString());
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenant);
        });

    private static HttpClient CreateTenantClient(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenant);
        return client;
    }

    private static Grpc.Core.Metadata TenantHeaders(string tenant) =>
        new() { { "x-custodex-tenant", tenant } };

    [Fact]
    public async Task Rest_list_objects_with_max_page_size_is_capped_without_error()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        await SeedThreeGrantedObjectsAsync(factory, tenant);

        var client = CreateTenantClient(factory, tenant);
        var req = new ListObjectsRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenant,
            ObjectType: "widget",
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null),
            PageSize: int.MaxValue);

        var resp = await client.PostAsJsonAsync("/api/list-objects", req);

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ListObjectsResponseDto>();
        result!.ObjectIds.Count.ShouldBe(ConfiguredMax);
        result.ContinuationToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Rest_list_subjects_with_max_page_size_is_capped_without_error()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        await SeedObjectWithThreeSubjectsAsync(factory, tenant);

        var client = CreateTenantClient(factory, tenant);
        var req = new ListSubjectsRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenant,
            Object: new EntityRefDto("widget", "shared"),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null),
            PageSize: int.MaxValue);

        var resp = await client.PostAsJsonAsync("/api/list-subjects", req);

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ListSubjectsResponseDto>();
        result!.Subjects.Count.ShouldBe(ConfiguredMax);
        result.ContinuationToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Grpc_list_objects_with_max_page_size_is_capped_without_error()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        await SeedThreeGrantedObjectsAsync(factory, tenant);

        var channel = factory.CreateAuthenticatedGrpcChannel();
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.ListObjectsRequest
        {
            Subject = new Proto.SubjectRef { Type = "user", Id = "u-1" },
            ObjectType = "widget",
            Permission = "view",
            Context = new Proto.RequestContext(),
            PageSize = int.MaxValue,
        };

        var resp = await client.ListObjectsAsync(req, headers: TenantHeaders(tenant));

        resp.ObjectIds.Count.ShouldBe(ConfiguredMax);
        resp.ContinuationToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Grpc_list_subjects_with_max_page_size_is_capped_without_error()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        await SeedObjectWithThreeSubjectsAsync(factory, tenant);

        var channel = factory.CreateAuthenticatedGrpcChannel();
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.ListSubjectsRequest
        {
            Object = new Proto.EntityRef { Type = "widget", Id = "shared" },
            Permission = "view",
            Context = new Proto.RequestContext(),
            PageSize = int.MaxValue,
        };

        var resp = await client.ListSubjectsAsync(req, headers: TenantHeaders(tenant));

        resp.Subjects.Count.ShouldBe(ConfiguredMax);
        resp.ContinuationToken.ShouldNotBeNullOrEmpty();
    }

    private static async Task SeedThreeGrantedObjectsAsync(WebApplicationFactory<Program> factory, string tenant) =>
        await ProvisionAsync(factory, tenant,
        [
            new RelationTuple(new EntityRef("widget", "a"), "owner", new SubjectRef("user", "u-1")),
            new RelationTuple(new EntityRef("widget", "b"), "owner", new SubjectRef("user", "u-1")),
            new RelationTuple(new EntityRef("widget", "c"), "owner", new SubjectRef("user", "u-1")),
        ]);

    private static async Task SeedObjectWithThreeSubjectsAsync(WebApplicationFactory<Program> factory, string tenant) =>
        await ProvisionAsync(factory, tenant,
        [
            new RelationTuple(new EntityRef("widget", "shared"), "owner", new SubjectRef("user", "a")),
            new RelationTuple(new EntityRef("widget", "shared"), "owner", new SubjectRef("user", "b")),
            new RelationTuple(new EntityRef("widget", "shared"), "owner", new SubjectRef("user", "c")),
        ]);

    private static async Task ProvisionAsync(
        WebApplicationFactory<Program> factory, string tenant, IReadOnlyList<RelationTuple> tuples)
    {
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
        await relMgr.WriteTuplesAsync(tc, "test", tuples);
    }
}
