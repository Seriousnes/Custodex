using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Custodex.Core;
using Custodex.Service.Rest;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class RestErrorHandlingTests(PostgresFixture pg)
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
        });

    [Fact]
    public async Task Check_against_undefined_permission_returns_400()
    {
        await using var factory = CreateFactory();
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        var adminClient = factory.CreateAuthenticatedClient();

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user")))
            .Build();
        var schemaJson = Custodex.Service.Mapping.SchemaJson.Serialize(schema);

        await adminClient.PostAsJsonAsync("/api/stores", new CreateStoreRequestDto(TestAuthHelper.AdminStore));
        await adminClient.PostAsJsonAsync("/api/tenants",
            new CreateTenantRequestDto(TestAuthHelper.AdminStore, tenantId));
        await adminClient.PutAsJsonAsync($"/api/schema/{TestAuthHelper.AdminStore}",
            new SetActiveSchemaRequestDto(schemaJson));

        var tenantClient = factory.CreateAuthenticatedClient();
        tenantClient.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenantId);

        var req = new CheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            Object: new EntityRefDto("doc", "d-1"),
            Permission: "no-such-permission",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null));

        var resp = await tenantClient.PostAsJsonAsync("/api/check", req);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Check_with_a_malformed_consistency_token_returns_400()
    {
        await using var factory = CreateFactory();
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        var adminClient = factory.CreateAuthenticatedClient();

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("view", p => p.Relation("owner")))
            .Build();
        var schemaJson = Custodex.Service.Mapping.SchemaJson.Serialize(schema);

        await adminClient.PostAsJsonAsync("/api/stores", new CreateStoreRequestDto(TestAuthHelper.AdminStore));
        await adminClient.PostAsJsonAsync("/api/tenants",
            new CreateTenantRequestDto(TestAuthHelper.AdminStore, tenantId));
        await adminClient.PutAsJsonAsync($"/api/schema/{TestAuthHelper.AdminStore}",
            new SetActiveSchemaRequestDto(schemaJson));

        var tenantClient = factory.CreateAuthenticatedClient();
        tenantClient.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenantId);

        var req = new CheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            Object: new EntityRefDto("doc", "d-1"),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null,
                Consistency: new ConsistencyDto("at-least-as-fresh", "not-a-real-token")));

        var resp = await tenantClient.PostAsJsonAsync("/api/check", req);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Internal_error_does_not_leak_exception_detail_in_500_response()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString",
                "Host=127.0.0.1;Port=1;Database=nope;Username=u;Password=p;Timeout=1;Command Timeout=1");
            b.UseSetting("Custodex:ApplyMigrationsOnStartup", "false");
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
        });

        var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", "t-1");

        var req = new CheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: "t-1",
            Object: new EntityRefDto("doc", "d-1"),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null));

        var resp = await client.PostAsJsonAsync("/api/check", req);

        resp.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString().ShouldBe("An unexpected error occurred.");
    }
}
