using Custodex.Abstractions;
using Custodex.Core;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Proto = Custodex.Api;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class DecisionServiceTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory(string tenant) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenant);
        });

    private static GrpcChannel CreateChannel(WebApplicationFactory<Program> factory) =>
        factory.CreateAuthenticatedGrpcChannel();

    private static Metadata TenantHeaders(string tenant) =>
        new() { { "x-custodex-tenant", tenant } };

    private static async Task ProvisionAsync(
        IServiceProvider services, Schema schema, string tenant)
    {
        var tc = new TenantContext(TestAuthHelper.AdminStore, tenant);

        var storeMgr = services.GetRequiredService<IStoreManager>();
        var tenantMgr = services.GetRequiredService<ITenantManager>();
        var schemaMgr = services.GetRequiredService<ISchemaManager>();

        await storeMgr.CreateStoreAsync(TestAuthHelper.AdminStore);
        await tenantMgr.CreateTenantAsync(tc);
        await schemaMgr.SetActiveSchemaAsync(TestAuthHelper.AdminStore, schema);
    }

    [Fact]
    public async Task Check_granted_subject_returns_allowed_true()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await ProvisionAsync(scope.ServiceProvider, schema, tenant);
        var tc = new TenantContext(TestAuthHelper.AdminStore, tenant);

        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", "1"), "owner", new SubjectRef("user", "alice")),
        ]);

        var channel = CreateChannel(factory);
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.CheckRequest
        {
            Object = new Proto.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new Proto.SubjectRef { Type = "user", Id = "alice" },
            Context = new Proto.RequestContext(),
        };

        var resp = await client.CheckAsync(req, headers: TenantHeaders(tenant));

        resp.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_with_at_least_as_fresh_token_from_the_write_sees_the_write()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await ProvisionAsync(scope.ServiceProvider, schema, tenant);
        var tc = new TenantContext(TestAuthHelper.AdminStore, tenant);

        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();
        var token = await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", "1"), "owner", new SubjectRef("user", "alice")),
        ]);

        token.Value.ShouldNotBeNullOrEmpty();

        var channel = CreateChannel(factory);
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.CheckRequest
        {
            Object = new Proto.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new Proto.SubjectRef { Type = "user", Id = "alice" },
            Context = new Proto.RequestContext
            {
                Consistency = new Proto.Consistency
                {
                    Mode = Proto.ConsistencyMode.AtLeastAsFresh,
                    Token = token.Value,
                },
            },
        };

        var resp = await client.CheckAsync(req, headers: TenantHeaders(tenant));

        resp.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_ungranted_subject_returns_allowed_false()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await ProvisionAsync(scope.ServiceProvider, schema, tenant);

        var channel = CreateChannel(factory);
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.CheckRequest
        {
            Object = new Proto.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new Proto.SubjectRef { Type = "user", Id = "bob" },
            Context = new Proto.RequestContext(),
        };

        var resp = await client.CheckAsync(req, headers: TenantHeaders(tenant));

        resp.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Check_with_a_malformed_consistency_token_returns_invalid_argument()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await ProvisionAsync(scope.ServiceProvider, schema, tenant);

        var channel = CreateChannel(factory);
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.CheckRequest
        {
            Object = new Proto.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new Proto.SubjectRef { Type = "user", Id = "alice" },
            Context = new Proto.RequestContext
            {
                Consistency = new Proto.Consistency
                {
                    Mode = Proto.ConsistencyMode.AtLeastAsFresh,
                    Token = "not-a-real-token",
                },
            },
        };

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.CheckAsync(req, headers: TenantHeaders(tenant)).ResponseAsync);
        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ListObjects_returns_both_granted_object_ids_sorted()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await ProvisionAsync(scope.ServiceProvider, schema, tenant);
        var tc = new TenantContext(TestAuthHelper.AdminStore, tenant);

        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", "a"), "owner", new SubjectRef("user", "alice")),
            new RelationTuple(new EntityRef("widget", "b"), "owner", new SubjectRef("user", "alice")),
        ]);

        var channel = CreateChannel(factory);
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.ListObjectsRequest
        {
            Subject = new Proto.SubjectRef { Type = "user", Id = "alice" },
            ObjectType = "widget",
            Permission = "view",
            Context = new Proto.RequestContext(),
        };

        var resp = await client.ListObjectsAsync(req, headers: TenantHeaders(tenant));

        var ids = resp.ObjectIds.OrderBy(x => x).ToList();
        ids.Count.ShouldBe(2);
        ids[0].ShouldBe("a");
        ids[1].ShouldBe("b");
    }

    [Fact]
    public async Task BatchCheck_returns_aligned_results()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await ProvisionAsync(scope.ServiceProvider, schema, tenant);
        var tc = new TenantContext(TestAuthHelper.AdminStore, tenant);

        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", "1"), "owner", new SubjectRef("user", "alice")),
        ]);

        var channel = CreateChannel(factory);
        var client = new Proto.Decision.DecisionClient(channel);
        var req = new Proto.BatchCheckRequest
        {
            Context = new Proto.RequestContext(),
        };
        req.Items.Add(new Proto.CheckItem
        {
            Object = new Proto.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new Proto.SubjectRef { Type = "user", Id = "alice" },
        });
        req.Items.Add(new Proto.CheckItem
        {
            Object = new Proto.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new Proto.SubjectRef { Type = "user", Id = "bob" },
        });

        var resp = await client.BatchCheckAsync(req, headers: TenantHeaders(tenant));

        resp.Results.Count.ShouldBe(2);
        resp.Results[0].Allowed.ShouldBeTrue();
        resp.Results[1].Allowed.ShouldBeFalse();
    }
}
