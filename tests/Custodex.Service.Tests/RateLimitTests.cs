using System.Net;
using System.Net.Http.Json;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Rest;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class RateLimitTests(PostgresFixture pg)
{
    private const string SecondKey = "test-second-caller-key";
    private const string SecondStore = "test-second-caller-store";

    private WebApplicationFactory<Program> CreateFactory(
        string tenant, int permitLimit, int windowSeconds, bool withSecondIdentity = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseSetting("Custodex:RateLimit:PermitLimit", permitLimit.ToString());
            b.UseSetting("Custodex:RateLimit:WindowSeconds", windowSeconds.ToString());
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenant);
            if (withSecondIdentity)
            {
                b.UseSetting("Custodex:ApiKeys:1:Key", SecondKey);
                b.UseSetting("Custodex:ApiKeys:1:Store", SecondStore);
                b.UseSetting("Custodex:ApiKeys:1:Role", "admin");
                b.UseSetting("Custodex:ApiKeys:1:Tenants:0", tenant);
            }
        });

    private WebApplicationFactory<Program> CreateDefaultFactory(string tenant) =>
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

    private static HttpClient CreateSecondIdentityClient(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", SecondKey);
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenant);
        return client;
    }

    private static CheckRequestDto CheckRequest(string store, string tenant, string objId) =>
        new(
            Store: store,
            Tenant: tenant,
            Object: new EntityRefDto("widget", objId),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null));

    [Fact]
    public async Task Rest_requests_over_the_configured_limit_return_429()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant, permitLimit: 1, windowSeconds: 60);
        var objId = await SetupMinimalSchema(factory, TestAuthHelper.AdminStore, tenant);

        var client = CreateTenantClient(factory, tenant);
        var req = CheckRequest(TestAuthHelper.AdminStore, tenant, objId);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/check", req);
            statuses.Add(resp.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Rest_a_different_authenticated_identity_is_not_throttled_by_another_callers_limit()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant, permitLimit: 1, windowSeconds: 60, withSecondIdentity: true);
        var firstObjId = await SetupMinimalSchema(factory, TestAuthHelper.AdminStore, tenant);
        var secondObjId = await SetupMinimalSchema(factory, SecondStore, tenant);

        var firstClient = CreateTenantClient(factory, tenant);
        var firstReq = CheckRequest(TestAuthHelper.AdminStore, tenant, firstObjId);

        (await firstClient.PostAsJsonAsync("/api/check", firstReq)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await firstClient.PostAsJsonAsync("/api/check", firstReq)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var secondClient = CreateSecondIdentityClient(factory, tenant);
        var secondReq = CheckRequest(SecondStore, tenant, secondObjId);

        var secondResp = await secondClient.PostAsJsonAsync("/api/check", secondReq);
        secondResp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rest_default_rate_limit_does_not_throttle_a_normal_request_sequence()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateDefaultFactory(tenant);
        var objId = await SetupMinimalSchema(factory, TestAuthHelper.AdminStore, tenant);

        var client = CreateTenantClient(factory, tenant);
        var req = CheckRequest(TestAuthHelper.AdminStore, tenant, objId);

        for (var i = 0; i < 20; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/check", req);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static async Task<string> SetupMinimalSchema(
        WebApplicationFactory<Program> factory, string store, string tenant)
    {
        var objId = $"obj-{Guid.NewGuid():N}";
        var tc = new TenantContext(store, tenant);

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
        await tenantMgr.CreateTenantAsync(tc);
        await schemaMgr.SetActiveSchemaAsync(store, schema);
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", objId), "owner", new SubjectRef("user", "u-1")),
        ]);

        return objId;
    }
}
