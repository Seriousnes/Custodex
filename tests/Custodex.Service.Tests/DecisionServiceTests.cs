using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Mapping;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ProtoV1 = Custodex.V1;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class DecisionServiceTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
        });

    private static GrpcChannel CreateChannel(WebApplicationFactory<Program> factory) =>
        GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        });

    private static async Task<(string Store, string Tenant)> ProvisionAsync(
        IServiceProvider services, Schema schema)
    {
        var store = $"store-{Guid.NewGuid():N}";
        var tenant = $"tenant-{Guid.NewGuid():N}";
        var tc = new TenantContext(store, tenant);

        var storeMgr = services.GetRequiredService<IStoreManager>();
        var tenantMgr = services.GetRequiredService<ITenantManager>();
        var schemaMgr = services.GetRequiredService<ISchemaManager>();

        await storeMgr.CreateStoreAsync(store);
        await tenantMgr.CreateTenantAsync(tc);
        await schemaMgr.SetActiveSchemaAsync(store, schema);

        return (store, tenant);
    }

    [Fact]
    public async Task Check_granted_subject_returns_allowed_true()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        var (store, tenant) = await ProvisionAsync(scope.ServiceProvider, schema);
        var tc = new TenantContext(store, tenant);

        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", "1"), "owner", new SubjectRef("user", "alice")),
        ]);

        var channel = CreateChannel(factory);
        var client = new ProtoV1.Decision.DecisionClient(channel);
        var req = new ProtoV1.CheckRequest
        {
            Tenant = new ProtoV1.TenantContext { Store = store, Tenant = tenant },
            Object = new ProtoV1.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new ProtoV1.SubjectRef { Type = "user", Id = "alice" },
            Context = new ProtoV1.RequestContext(),
        };

        var resp = await client.CheckAsync(req);

        resp.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_ungranted_subject_returns_allowed_false()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        var (store, tenant) = await ProvisionAsync(scope.ServiceProvider, schema);

        var channel = CreateChannel(factory);
        var client = new ProtoV1.Decision.DecisionClient(channel);
        var req = new ProtoV1.CheckRequest
        {
            Tenant = new ProtoV1.TenantContext { Store = store, Tenant = tenant },
            Object = new ProtoV1.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new ProtoV1.SubjectRef { Type = "user", Id = "bob" },
            Context = new ProtoV1.RequestContext(),
        };

        var resp = await client.CheckAsync(req);

        resp.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ListObjects_returns_both_granted_object_ids_sorted()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        var (store, tenant) = await ProvisionAsync(scope.ServiceProvider, schema);
        var tc = new TenantContext(store, tenant);

        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", "a"), "owner", new SubjectRef("user", "alice")),
            new RelationTuple(new EntityRef("widget", "b"), "owner", new SubjectRef("user", "alice")),
        ]);

        var channel = CreateChannel(factory);
        var client = new ProtoV1.Decision.DecisionClient(channel);
        var req = new ProtoV1.ListObjectsRequest
        {
            Tenant = new ProtoV1.TenantContext { Store = store, Tenant = tenant },
            Subject = new ProtoV1.SubjectRef { Type = "user", Id = "alice" },
            ObjectType = "widget",
            Permission = "view",
            Context = new ProtoV1.RequestContext(),
        };

        var resp = await client.ListObjectsAsync(req);

        var ids = resp.ObjectIds.OrderBy(x => x).ToList();
        ids.Count.ShouldBe(2);
        ids[0].ShouldBe("a");
        ids[1].ShouldBe("b");
    }

    [Fact]
    public async Task BatchCheck_returns_aligned_results()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        var (store, tenant) = await ProvisionAsync(scope.ServiceProvider, schema);
        var tc = new TenantContext(store, tenant);

        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef("widget", "1"), "owner", new SubjectRef("user", "alice")),
        ]);

        var channel = CreateChannel(factory);
        var client = new ProtoV1.Decision.DecisionClient(channel);
        var req = new ProtoV1.BatchCheckRequest
        {
            Tenant = new ProtoV1.TenantContext { Store = store, Tenant = tenant },
            Context = new ProtoV1.RequestContext(),
        };
        req.Items.Add(new ProtoV1.CheckItem
        {
            Object = new ProtoV1.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new ProtoV1.SubjectRef { Type = "user", Id = "alice" },
        });
        req.Items.Add(new ProtoV1.CheckItem
        {
            Object = new ProtoV1.EntityRef { Type = "widget", Id = "1" },
            Permission = "view",
            Subject = new ProtoV1.SubjectRef { Type = "user", Id = "bob" },
        });

        var resp = await client.BatchCheckAsync(req);

        resp.Results.Count.ShouldBe(2);
        resp.Results[0].Allowed.ShouldBeTrue();
        resp.Results[1].Allowed.ShouldBeFalse();
    }
}
