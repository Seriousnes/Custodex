using System.Net;
using System.Net.Http.Json;
using Custodex.Core;
using Custodex.Service.Mapping;
using Custodex.Service.Rest;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using ProtoV1 = Custodex.V1;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class ControlPlaneStoreScopeTests(PostgresFixture pg)
{
    private const string OtherStore = "other-store";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
        });

    private static GrpcChannel CreateChannel(WebApplicationFactory<Program> factory) =>
        factory.CreateAuthenticatedGrpcChannel();

    [Fact]
    public async Task SetActive_targeting_a_different_store_is_denied()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);
        var sClient = new ProtoV1.Schema.SchemaClient(channel);

        await pClient.CreateStoreAsync(new ProtoV1.CreateStoreRequest { Store = OtherStore });

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await sClient.SetActiveAsync(new ProtoV1.SetActiveSchemaRequest
            {
                Store = OtherStore,
                SchemaJson = SchemaJson.Serialize(schema),
            }));

        ex.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task GetActive_targeting_a_different_store_is_denied()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);
        var sClient = new ProtoV1.Schema.SchemaClient(channel);

        await pClient.CreateStoreAsync(new ProtoV1.CreateStoreRequest { Store = OtherStore });

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await sClient.GetActiveAsync(new ProtoV1.GetActiveSchemaRequest { Store = OtherStore }));

        ex.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task CreateTenant_targeting_a_different_store_is_denied()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);

        await pClient.CreateStoreAsync(new ProtoV1.CreateStoreRequest { Store = OtherStore });

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await pClient.CreateTenantAsync(new ProtoV1.CreateTenantRequest
            {
                Tenant = new ProtoV1.TenantContext
                {
                    Store = OtherStore,
                    Tenant = $"t-{Guid.NewGuid():N}",
                },
            }));

        ex.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task SetActive_targeting_the_authenticated_store_succeeds()
    {
        await using var factory = CreateFactory();
        var channel = CreateChannel(factory);
        var pClient = new ProtoV1.Provisioning.ProvisioningClient(channel);
        var sClient = new ProtoV1.Schema.SchemaClient(channel);

        await pClient.CreateStoreAsync(new ProtoV1.CreateStoreRequest { Store = TestAuthHelper.AdminStore });

        var schema = new SchemaBuilder("v1")
            .Type("widget", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();

        await sClient.SetActiveAsync(new ProtoV1.SetActiveSchemaRequest
        {
            Store = TestAuthHelper.AdminStore,
            SchemaJson = SchemaJson.Serialize(schema),
        });

        var resp = await sClient.GetActiveAsync(
            new ProtoV1.GetActiveSchemaRequest { Store = TestAuthHelper.AdminStore });

        resp.Found.ShouldBeTrue();
    }

    [Fact]
    public async Task Rest_set_schema_targeting_a_different_store_returns_403()
    {
        await using var factory = CreateFactory();
        var adminClient = factory.CreateAuthenticatedClient();

        await adminClient.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(OtherStore));

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("edit", p => p.Relation("owner")))
            .Build();
        var schemaJson = SchemaJson.Serialize(schema);

        var resp = await adminClient.PutAsJsonAsync(
            $"/v1/schema/{OtherStore}", new SetActiveSchemaRequestDto(schemaJson));

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rest_get_schema_targeting_a_different_store_returns_403()
    {
        await using var factory = CreateFactory();
        var adminClient = factory.CreateAuthenticatedClient();

        await adminClient.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(OtherStore));

        var resp = await adminClient.GetAsync($"/v1/schema/{OtherStore}");

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rest_create_tenant_targeting_a_different_store_returns_403()
    {
        await using var factory = CreateFactory();
        var adminClient = factory.CreateAuthenticatedClient();

        await adminClient.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(OtherStore));

        var resp = await adminClient.PostAsJsonAsync("/v1/tenants",
            new CreateTenantRequestDto(OtherStore, $"t-{Guid.NewGuid():N}"));

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rest_set_schema_targeting_the_authenticated_store_succeeds()
    {
        await using var factory = CreateFactory();
        var adminClient = factory.CreateAuthenticatedClient();

        await adminClient.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(TestAuthHelper.AdminStore));

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("edit", p => p.Relation("owner")))
            .Build();
        var schemaJson = SchemaJson.Serialize(schema);

        var resp = await adminClient.PutAsJsonAsync(
            $"/v1/schema/{TestAuthHelper.AdminStore}", new SetActiveSchemaRequestDto(schemaJson));

        resp.EnsureSuccessStatusCode();
    }
}
