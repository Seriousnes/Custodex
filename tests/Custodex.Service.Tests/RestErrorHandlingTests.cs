using System.Net;
using System.Net.Http.Json;
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

        await adminClient.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(TestAuthHelper.AdminStore));
        await adminClient.PostAsJsonAsync("/v1/tenants",
            new CreateTenantRequestDto(TestAuthHelper.AdminStore, tenantId));
        await adminClient.PutAsJsonAsync($"/v1/schema/{TestAuthHelper.AdminStore}",
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

        var resp = await tenantClient.PostAsJsonAsync("/v1/check", req);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
