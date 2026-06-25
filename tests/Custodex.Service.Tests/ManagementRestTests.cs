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
        });

    [Fact]
    public async Task Provision_set_schema_write_read_tuples_and_read_change_log()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var storeId = $"store-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("edit", p => p.Relation("owner")))
            .Build();
        var schemaJson = Custodex.Service.Mapping.SchemaJson.Serialize(schema);

        var createStore = await client.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(storeId));
        createStore.StatusCode.ShouldBe(HttpStatusCode.Created);

        var createTenant = await client.PostAsJsonAsync("/v1/tenants", new CreateTenantRequestDto(storeId, tenantId));
        createTenant.StatusCode.ShouldBe(HttpStatusCode.Created);

        var setSchema = await client.PutAsJsonAsync($"/v1/schema/{storeId}", new SetActiveSchemaRequestDto(schemaJson));
        setSchema.EnsureSuccessStatusCode();

        var objId = $"obj-{Guid.NewGuid():N}";
        var tuple = new RelationTupleDto(
            Object: new EntityRefDto("doc", objId),
            Relation: "owner",
            Subject: new SubjectRefDto("user", "u-1", null),
            Condition: null);
        var writeTuples = await client.PostAsJsonAsync("/v1/tuples",
            new WriteTuplesRequestDto(storeId, tenantId, "test", [tuple]));
        writeTuples.EnsureSuccessStatusCode();

        var readTuples = await client.PostAsJsonAsync("/v1/tuples/query",
            new ReadTuplesRequestDto(storeId, tenantId, ObjectType: "doc"));
        readTuples.EnsureSuccessStatusCode();
        var tupleResult = await readTuples.Content.ReadFromJsonAsync<ReadTuplesResponseDto>();
        tupleResult!.Tuples.ShouldNotBeEmpty();
        tupleResult.Tuples.ShouldContain(t => t.Object.Id == objId);

        var readLog = await client.PostAsJsonAsync("/v1/change-log/query",
            new ReadChangeLogRequestDto(storeId, tenantId, Limit: 50));
        readLog.EnsureSuccessStatusCode();
        var logResult = await readLog.Content.ReadFromJsonAsync<ReadChangeLogResponseDto>();
        logResult!.Entries.ShouldNotBeEmpty();
        logResult.Entries.ShouldContain(e => e.Operation == "write");
    }

    [Fact]
    public async Task SetActive_schema_with_invalid_reference_returns_400()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var storeId = $"store-{Guid.NewGuid():N}";
        var createStore = await client.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(storeId));
        createStore.StatusCode.ShouldBe(HttpStatusCode.Created);

        var badSchema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Permission("edit", p => p.Relation("nonexistent")))
            .Build();
        var badJson = Custodex.Service.Mapping.SchemaJson.Serialize(badSchema);

        var resp = await client.PutAsJsonAsync($"/v1/schema/{storeId}", new SetActiveSchemaRequestDto(badJson));
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Write_then_delete_tuple_leaves_query_empty()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var storeId = $"store-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("edit", p => p.Relation("owner")))
            .Build();
        var schemaJson = Custodex.Service.Mapping.SchemaJson.Serialize(schema);

        await client.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(storeId));
        await client.PostAsJsonAsync("/v1/tenants", new CreateTenantRequestDto(storeId, tenantId));
        await client.PutAsJsonAsync($"/v1/schema/{storeId}", new SetActiveSchemaRequestDto(schemaJson));

        var objId = $"obj-{Guid.NewGuid():N}";
        var tuple = new RelationTupleDto(
            Object: new EntityRefDto("doc", objId),
            Relation: "owner",
            Subject: new SubjectRefDto("user", "u-2", null),
            Condition: null);

        await client.PostAsJsonAsync("/v1/tuples",
            new WriteTuplesRequestDto(storeId, tenantId, "test", [tuple]));

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/v1/tuples")
        {
            Content = JsonContent.Create(new DeleteTuplesRequestDto(storeId, tenantId, "test", [tuple])),
        });

        var readTuples = await client.PostAsJsonAsync("/v1/tuples/query",
            new ReadTuplesRequestDto(storeId, tenantId, ObjectType: "doc", ObjectId: objId));
        var result = await readTuples.Content.ReadFromJsonAsync<ReadTuplesResponseDto>();

        result!.Tuples.ShouldBeEmpty();
    }
}
