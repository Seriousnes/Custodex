using System.Net;
using System.Net.Http.Json;
using Custodex.Core;
using Custodex.Service.Rest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class ManagementRestTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
        });

    private static HttpClient CreateTenantClient(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenant);
        return client;
    }

    [Fact]
    public async Task Provision_set_schema_write_read_tuples_and_read_change_log()
    {
        await using var factory = CreateFactory();
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var adminClient = factory.CreateAuthenticatedClient();
        var tenantClient = CreateTenantClient(factory, tenantId);

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("edit", p => p.Relation("owner")))
            .Build();
        var schemaJson = Custodex.Service.Mapping.SchemaJson.Serialize(schema);

        var createStore = await adminClient.PostAsJsonAsync("/v1/stores",
            new CreateStoreRequestDto(TestAuthHelper.AdminStore));
        createStore.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);

        var createTenant = await adminClient.PostAsJsonAsync("/v1/tenants",
            new CreateTenantRequestDto(TestAuthHelper.AdminStore, tenantId));
        createTenant.StatusCode.ShouldBe(HttpStatusCode.Created);

        var setSchema = await adminClient.PutAsJsonAsync(
            $"/v1/schema/{TestAuthHelper.AdminStore}", new SetActiveSchemaRequestDto(schemaJson));
        setSchema.EnsureSuccessStatusCode();

        var objId = $"obj-{Guid.NewGuid():N}";
        var tuple = new RelationTupleDto(
            Object: new EntityRefDto("doc", objId),
            Relation: "owner",
            Subject: new SubjectRefDto("user", "u-1", null),
            Condition: null);
        var writeTuples = await tenantClient.PostAsJsonAsync("/v1/tuples",
            new WriteTuplesRequestDto(TestAuthHelper.AdminStore, tenantId, "test", [tuple]));
        writeTuples.EnsureSuccessStatusCode();

        var readTuples = await tenantClient.PostAsJsonAsync("/v1/tuples/query",
            new ReadTuplesRequestDto(TestAuthHelper.AdminStore, tenantId, ObjectType: "doc"));
        readTuples.EnsureSuccessStatusCode();
        var tupleResult = await readTuples.Content.ReadFromJsonAsync<ReadTuplesResponseDto>();
        tupleResult!.Tuples.ShouldNotBeEmpty();
        tupleResult.Tuples.ShouldContain(t => t.Object.Id == objId);

        var readLog = await tenantClient.PostAsJsonAsync("/v1/change-log/query",
            new ReadChangeLogRequestDto(TestAuthHelper.AdminStore, tenantId, Limit: 50));
        readLog.EnsureSuccessStatusCode();
        var logResult = await readLog.Content.ReadFromJsonAsync<ReadChangeLogResponseDto>();
        logResult!.Entries.ShouldNotBeEmpty();
        logResult.Entries.ShouldContain(e => e.Operation == "write");
    }

    [Fact]
    public async Task SetActive_schema_with_invalid_reference_returns_400()
    {
        await using var factory = CreateFactory();
        var adminClient = factory.CreateAuthenticatedClient();

        var createStore = await adminClient.PostAsJsonAsync("/v1/stores",
            new CreateStoreRequestDto(TestAuthHelper.AdminStore));
        createStore.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);

        var badSchema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Permission("edit", p => p.Relation("nonexistent")))
            .Build();
        var badJson = Custodex.Service.Mapping.SchemaJson.Serialize(badSchema);

        var resp = await adminClient.PutAsJsonAsync(
            $"/v1/schema/{TestAuthHelper.AdminStore}", new SetActiveSchemaRequestDto(badJson));
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Write_then_delete_tuple_leaves_query_empty()
    {
        await using var factory = CreateFactory();
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var adminClient = factory.CreateAuthenticatedClient();
        var tenantClient = CreateTenantClient(factory, tenantId);

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("edit", p => p.Relation("owner")))
            .Build();
        var schemaJson = Custodex.Service.Mapping.SchemaJson.Serialize(schema);

        await adminClient.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(TestAuthHelper.AdminStore));
        await adminClient.PostAsJsonAsync("/v1/tenants",
            new CreateTenantRequestDto(TestAuthHelper.AdminStore, tenantId));
        await adminClient.PutAsJsonAsync($"/v1/schema/{TestAuthHelper.AdminStore}",
            new SetActiveSchemaRequestDto(schemaJson));

        var objId = $"obj-{Guid.NewGuid():N}";
        var tuple = new RelationTupleDto(
            Object: new EntityRefDto("doc", objId),
            Relation: "owner",
            Subject: new SubjectRefDto("user", "u-2", null),
            Condition: null);

        await tenantClient.PostAsJsonAsync("/v1/tuples",
            new WriteTuplesRequestDto(TestAuthHelper.AdminStore, tenantId, "test", [tuple]));

        await tenantClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/v1/tuples")
        {
            Content = JsonContent.Create(new DeleteTuplesRequestDto(TestAuthHelper.AdminStore, tenantId, "test", [tuple])),
        });

        var readTuples = await tenantClient.PostAsJsonAsync("/v1/tuples/query",
            new ReadTuplesRequestDto(TestAuthHelper.AdminStore, tenantId, ObjectType: "doc", ObjectId: objId));
        var result = await readTuples.Content.ReadFromJsonAsync<ReadTuplesResponseDto>();

        result!.Tuples.ShouldBeEmpty();
    }
}
