using System.Net;
using System.Net.Http.Json;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Rest;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Proto = Custodex.Api;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class BatchCheckItemLimitTests(PostgresFixture pg)
{
    private const int ConfiguredMax = 2;

    private WebApplicationFactory<Program> CreateFactory(string tenant) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseSetting("Custodex:MaxBatchItems", ConfiguredMax.ToString());
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenant);
        });

    private static HttpClient CreateTenantClient(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenant);
        return client;
    }

    private static Metadata TenantHeaders(string tenant) =>
        new() { { "x-custodex-tenant", tenant } };

    [Fact]
    public async Task Rest_batch_check_over_the_configured_max_returns_400()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await SetupMinimalSchema(factory, tenant);

        var client = CreateTenantClient(factory, tenant);
        var ctx = new RequestContextDto(
            Subject: new SubjectRefDto("user", "u-1", null),
            Now: null,
            Attributes: null);
        var req = new BatchCheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenant,
            Checks:
            [
                new CheckItemDto(new EntityRefDto("widget", objId), "view"),
                new CheckItemDto(new EntityRefDto("widget", objId), "view"),
                new CheckItemDto(new EntityRefDto("widget", objId), "view"),
            ],
            Context: ctx);

        var resp = await client.PostAsJsonAsync("/api/batch-check", req);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rest_batch_check_at_the_configured_max_succeeds()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await SetupMinimalSchema(factory, tenant);

        var client = CreateTenantClient(factory, tenant);
        var ctx = new RequestContextDto(
            Subject: new SubjectRefDto("user", "u-1", null),
            Now: null,
            Attributes: null);
        var req = new BatchCheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenant,
            Checks:
            [
                new CheckItemDto(new EntityRefDto("widget", objId), "view"),
                new CheckItemDto(new EntityRefDto("widget", objId), "view"),
            ],
            Context: ctx);

        var resp = await client.PostAsJsonAsync("/api/batch-check", req);

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<BatchCheckResponseDto>();
        result!.Results.Count.ShouldBe(ConfiguredMax);
        result.Results.ShouldAllBe(r => r.Allowed);
    }

    [Fact]
    public async Task Grpc_batch_check_over_the_configured_max_returns_invalid_argument()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await SetupMinimalSchema(factory, tenant);

        var channel = factory.CreateAuthenticatedGrpcChannel();
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.BatchCheckRequest { Context = new Proto.RequestContext() };
        for (var i = 0; i < ConfiguredMax + 1; i++)
        {
            req.Items.Add(new Proto.CheckItem
            {
                Object = new Proto.EntityRef { Type = "widget", Id = objId },
                Permission = "view",
                Subject = new Proto.SubjectRef { Type = "user", Id = "u-1" },
            });
        }

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.BatchCheckAsync(req, headers: TenantHeaders(tenant)).ResponseAsync);

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Grpc_batch_check_at_the_configured_max_succeeds()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await SetupMinimalSchema(factory, tenant);

        var channel = factory.CreateAuthenticatedGrpcChannel();
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.BatchCheckRequest { Context = new Proto.RequestContext() };
        for (var i = 0; i < ConfiguredMax; i++)
        {
            req.Items.Add(new Proto.CheckItem
            {
                Object = new Proto.EntityRef { Type = "widget", Id = objId },
                Permission = "view",
                Subject = new Proto.SubjectRef { Type = "user", Id = "u-1" },
            });
        }

        var resp = await client.BatchCheckAsync(req, headers: TenantHeaders(tenant));

        resp.Results.Count.ShouldBe(ConfiguredMax);
        resp.Results.ShouldAllBe(r => r.Allowed);
    }

    private static async Task<string> SetupMinimalSchema(WebApplicationFactory<Program> factory, string tenant)
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
