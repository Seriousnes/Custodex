using Custodex.Core;
using Custodex.Service.Mapping;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

using Proto = Custodex.Api;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class ManagementServiceTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory(params string[] tenants) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenants);
        });

    private static GrpcChannel CreateChannel(WebApplicationFactory<Program> factory) =>
        factory.CreateAuthenticatedGrpcChannel();

    private static Metadata TenantHeaders(string tenant) =>
        new() { { "x-custodex-tenant", tenant } };

    private static async Task ProvisionStoreAndTenantAsync(
        Proto.Provisioning.ProvisioningClient pClient, string tenant)
    {
        await pClient.CreateStoreAsync(
            new Proto.CreateStoreRequest { Store = TestAuthHelper.AdminStore });
        await pClient.CreateTenantAsync(new Proto.CreateTenantRequest
        {
            Tenant = new Proto.TenantContext
            {
                Store = TestAuthHelper.AdminStore,
                Tenant = tenant,
            },
        });
    }

    [Fact]
    public async Task Provision_SetActive_GetActive_round_trips_schema()
    {
        var tenant = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new Proto.Provisioning.ProvisioningClient(channel);
        var sClient = new Proto.Schema.SchemaClient(channel);

        await ProvisionStoreAndTenantAsync(pClient, tenant);

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        var json = SchemaJson.Serialize(schema);
        await sClient.SetActiveAsync(new Proto.SetActiveSchemaRequest
        {
            Store = TestAuthHelper.AdminStore,
            SchemaJson = json,
        });

        var resp = await sClient.GetActiveAsync(
            new Proto.GetActiveSchemaRequest { Store = TestAuthHelper.AdminStore });

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
        var tenant = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var channel = CreateChannel(factory);
        var pClient = new Proto.Provisioning.ProvisioningClient(channel);
        var sClient = new Proto.Schema.SchemaClient(channel);
        var rClient = new Proto.Relations.RelationsClient(channel);

        await ProvisionStoreAndTenantAsync(pClient, tenant);

        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.Type("user")))
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user").SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await sClient.SetActiveAsync(new Proto.SetActiveSchemaRequest
        {
            Store = TestAuthHelper.AdminStore,
            SchemaJson = SchemaJson.Serialize(schema),
        });

        var tenantMeta = TenantHeaders(tenant);
        await rClient.WriteTuplesAsync(new Proto.WriteTuplesRequest
        {
            Actor = "test",
            Tuples =
            {
                new Proto.RelationTuple
                {
                    Object = new Proto.EntityRef { Type = "widget", Id = "1" },
                    Relation = "owner",
                    Subject = new Proto.SubjectRef { Type = "group", Id = "editors", Relation = "member" },
                },
            },
        }, headers: tenantMeta);

        var readResp = await rClient.ReadTuplesAsync(new Proto.ReadTuplesRequest
        {
            Filter = new Proto.TupleFilter { ObjectType = "widget" },
        }, headers: tenantMeta);

        readResp.Tuples.Count.ShouldBe(1);
        readResp.Tuples[0].Subject.Relation.ShouldBe("member");
    }

    [Fact]
    public async Task WriteTuples_returns_a_consistency_token_scoped_to_the_tenant()
    {
        var tenant = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var channel = CreateChannel(factory);
        var pClient = new Proto.Provisioning.ProvisioningClient(channel);
        var sClient = new Proto.Schema.SchemaClient(channel);
        var rClient = new Proto.Relations.RelationsClient(channel);

        await ProvisionStoreAndTenantAsync(pClient, tenant);

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t.Relation("owner", s => s.Type("user")))
            .Build();
        await sClient.SetActiveAsync(new Proto.SetActiveSchemaRequest
        {
            Store = TestAuthHelper.AdminStore,
            SchemaJson = SchemaJson.Serialize(schema),
        });

        var writeResp = await rClient.WriteTuplesAsync(new Proto.WriteTuplesRequest
        {
            Actor = "test",
            Tuples =
            {
                new Proto.RelationTuple
                {
                    Object = new Proto.EntityRef { Type = "widget", Id = "1" },
                    Relation = "owner",
                    Subject = new Proto.SubjectRef { Type = "user", Id = "alice" },
                },
            },
        }, headers: TenantHeaders(tenant));

        writeResp.ConsistencyToken.ShouldNotBeNullOrEmpty();
        var parts = new Abstractions.ConsistencyToken(writeResp.ConsistencyToken).Decode();
        parts.Tenant.ShouldBe(tenant);
        parts.Epoch.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReadChangeLog_records_authenticated_caller_ignoring_spoofed_actor()
    {
        var tenant = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var channel = CreateChannel(factory);
        var pClient = new Proto.Provisioning.ProvisioningClient(channel);
        var sClient = new Proto.Schema.SchemaClient(channel);
        var rClient = new Proto.Relations.RelationsClient(channel);

        await ProvisionStoreAndTenantAsync(pClient, tenant);

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t.Relation("owner", s => s.Type("user")))
            .Build();
        await sClient.SetActiveAsync(new Proto.SetActiveSchemaRequest
        {
            Store = TestAuthHelper.AdminStore,
            SchemaJson = SchemaJson.Serialize(schema),
        });

        const string spoofed = "attacker-chosen-victim";
        var authenticated = $"apikey:{TestAuthHelper.AdminStore}";

        var tenantMeta = TenantHeaders(tenant);
        await rClient.WriteTuplesAsync(new Proto.WriteTuplesRequest
        {
            Actor = spoofed,
            Tuples =
            {
                new Proto.RelationTuple
                {
                    Object = new Proto.EntityRef { Type = "widget", Id = "1" },
                    Relation = "owner",
                    Subject = new Proto.SubjectRef { Type = "user", Id = "alice" },
                },
            },
        }, headers: tenantMeta);

        var logResp = await rClient.ReadChangeLogAsync(new Proto.ReadChangeLogRequest
        {
            Limit = 10,
        }, headers: tenantMeta);

        logResp.Entries.ShouldNotBeEmpty();
        logResp.Entries.ShouldContain(e => e.Actor == authenticated);
        logResp.Entries.ShouldAllBe(e => e.Actor != spoofed);
    }

    [Fact]
    public async Task SetActive_with_missing_relation_returns_InvalidArgument()
    {
        var tenant = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new Proto.Provisioning.ProvisioningClient(channel);
        var sClient = new Proto.Schema.SchemaClient(channel);

        await ProvisionStoreAndTenantAsync(pClient, tenant);

        var badSchema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Permission("view", p => p.Relation("nonexistent")))
            .Build();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await sClient.SetActiveAsync(new Proto.SetActiveSchemaRequest
            {
                Store = TestAuthHelper.AdminStore,
                SchemaJson = SchemaJson.Serialize(badSchema),
            }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SetActive_with_malformed_json_returns_InvalidArgument()
    {
        var tenant = $"t-{Guid.NewGuid():N}";
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new Proto.Provisioning.ProvisioningClient(channel);
        var sClient = new Proto.Schema.SchemaClient(channel);

        await ProvisionStoreAndTenantAsync(pClient, tenant);

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await sClient.SetActiveAsync(new Proto.SetActiveSchemaRequest
            {
                Store = TestAuthHelper.AdminStore,
                SchemaJson = "{ not valid schema json",
            }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }
}
