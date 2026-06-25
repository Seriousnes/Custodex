using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Mapping;
using Grpc.Net.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ProtoV1 = Custodex.V1;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class ManagementServiceTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
        });

    private static GrpcChannel CreateChannel(WebApplicationFactory<Program> factory) =>
        factory.CreateAuthenticatedGrpcChannel();

    private static async Task<(string Store, string Tenant)> ProvisionStoreAndTenantAsync(
        ProtoV1.Provisioning.ProvisioningClient pClient)
    {
        var store = $"s-{Guid.NewGuid():N}";
        var tenant = $"t-{Guid.NewGuid():N}";
        await pClient.CreateStoreAsync(new ProtoV1.CreateStoreRequest { Store = store });
        await pClient.CreateTenantAsync(new ProtoV1.CreateTenantRequest
        {
            Tenant = new ProtoV1.TenantContext { Store = store, Tenant = tenant },
        });
        return (store, tenant);
    }

    [Fact]
    public async Task Provision_SetActive_GetActive_round_trips_schema()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);
        var sClient = new ProtoV1.Schema.SchemaClient(channel);

        var (store, _) = await ProvisionStoreAndTenantAsync(pClient);

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        var json = SchemaJson.Serialize(schema);
        await sClient.SetActiveAsync(new ProtoV1.SetActiveSchemaRequest { Store = store, SchemaJson = json });

        var resp = await sClient.GetActiveAsync(new ProtoV1.GetActiveSchemaRequest { Store = store });

        resp.Found.ShouldBeTrue();
        resp.SchemaJson.ShouldNotBeNullOrEmpty();
        var back = SchemaJson.Deserialize(resp.SchemaJson);
        back.ShouldNotBeNull();
        back!.Types.Count.ShouldBe(1);
        back.Types[0].Name.ShouldBe("widget");
    }

    [Fact]
    public async Task WriteTuples_then_ReadTuples_returns_tuple_with_subject_relation()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);
        var sClient = new ProtoV1.Schema.SchemaClient(channel);
        var rClient = new ProtoV1.Relations.RelationsClient(channel);

        var (store, tenant) = await ProvisionStoreAndTenantAsync(pClient);

        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.Type("user")))
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user").SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await sClient.SetActiveAsync(new ProtoV1.SetActiveSchemaRequest
        {
            Store = store,
            SchemaJson = SchemaJson.Serialize(schema),
        });

        var tc = new ProtoV1.TenantContext { Store = store, Tenant = tenant };
        await rClient.WriteTuplesAsync(new ProtoV1.WriteTuplesRequest
        {
            Tenant = tc,
            Actor = "test",
            Tuples =
            {
                new ProtoV1.RelationTuple
                {
                    Object = new ProtoV1.EntityRef { Type = "widget", Id = "1" },
                    Relation = "owner",
                    Subject = new ProtoV1.SubjectRef { Type = "group", Id = "editors", Relation = "member" },
                },
            },
        });

        var readResp = await rClient.ReadTuplesAsync(new ProtoV1.ReadTuplesRequest
        {
            Tenant = tc,
            Filter = new ProtoV1.TupleFilter { ObjectType = "widget" },
        });

        readResp.Tuples.Count.ShouldBe(1);
        readResp.Tuples[0].Subject.Relation.ShouldBe("member");
    }

    [Fact]
    public async Task ReadChangeLog_after_write_contains_entry_with_actor()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);
        var sClient = new ProtoV1.Schema.SchemaClient(channel);
        var rClient = new ProtoV1.Relations.RelationsClient(channel);

        var (store, tenant) = await ProvisionStoreAndTenantAsync(pClient);

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t.Relation("owner", s => s.Type("user")))
            .Build();
        await sClient.SetActiveAsync(new ProtoV1.SetActiveSchemaRequest
        {
            Store = store,
            SchemaJson = SchemaJson.Serialize(schema),
        });

        var tc = new ProtoV1.TenantContext { Store = store, Tenant = tenant };
        await rClient.WriteTuplesAsync(new ProtoV1.WriteTuplesRequest
        {
            Tenant = tc,
            Actor = "audit-actor",
            Tuples =
            {
                new ProtoV1.RelationTuple
                {
                    Object = new ProtoV1.EntityRef { Type = "widget", Id = "1" },
                    Relation = "owner",
                    Subject = new ProtoV1.SubjectRef { Type = "user", Id = "alice" },
                },
            },
        });

        var logResp = await rClient.ReadChangeLogAsync(new ProtoV1.ReadChangeLogRequest
        {
            Tenant = tc,
            Limit = 10,
        });

        logResp.Entries.ShouldNotBeEmpty();
        logResp.Entries.Any(e => e.Actor == "audit-actor").ShouldBeTrue();
    }

    [Fact]
    public async Task SetActive_with_missing_relation_returns_InvalidArgument()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);
        var sClient = new ProtoV1.Schema.SchemaClient(channel);

        var (store, _) = await ProvisionStoreAndTenantAsync(pClient);

        var badSchema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Permission("view", p => p.Relation("nonexistent")))
            .Build();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await sClient.SetActiveAsync(new ProtoV1.SetActiveSchemaRequest
            {
                Store = store,
                SchemaJson = SchemaJson.Serialize(badSchema),
            }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }
}
