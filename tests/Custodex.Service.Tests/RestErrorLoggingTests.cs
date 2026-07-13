using System.Net;
using System.Net.Http.Json;

using Custodex.Core;
using Custodex.Service.Rest;
using Custodex.Service.Tests.Auth;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class RestErrorLoggingTests(PostgresFixture pg)
{
    private const string ProblemDetailsCategory = "Custodex.Service.Rest.ProblemDetails";

    private WebApplicationFactory<Program> CreateFactory(CapturingLoggerProvider capture, params string[] tenants) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenants);
            b.ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.SetMinimumLevel(LogLevel.Warning);
                lb.AddProvider(capture);
            });
        });

    [Fact]
    public async Task Check_against_undefined_permission_logs_a_warning_with_status_and_tenant_context()
    {
        var capture = new CapturingLoggerProvider();
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(capture, tenantId);

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

        var warnings = capture.Entries
            .Where(e => e.Category == ProblemDetailsCategory && e.Level == LogLevel.Warning)
            .ToArray();

        warnings.ShouldNotBeEmpty();
        warnings.ShouldContain(e =>
            e.Message.Contains("400") && e.Message.Contains(tenantId) && e.Message.Contains("apikey:"));
        warnings.ShouldAllBe(e => !e.Message.Contains("d-1") && !e.Message.Contains("u-1"));
    }

    [Fact]
    public async Task Check_exceeding_the_evaluation_depth_bound_logs_a_warning_at_422()
    {
        var capture = new CapturingLoggerProvider();
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(capture, tenantId);

        var adminClient = factory.CreateAuthenticatedClient();

        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("owner", s => s.Type("user"))
                .Relation("parent", s => s.Type("doc"))
                .Permission("view", p => p.Relation("owner").Union(x => x.Arrow("parent", "view"))))
            .Build();
        var schemaJson = Custodex.Service.Mapping.SchemaJson.Serialize(schema);

        await adminClient.PostAsJsonAsync("/api/stores", new CreateStoreRequestDto(TestAuthHelper.AdminStore));
        await adminClient.PostAsJsonAsync("/api/tenants",
            new CreateTenantRequestDto(TestAuthHelper.AdminStore, tenantId));
        await adminClient.PutAsJsonAsync($"/api/schema/{TestAuthHelper.AdminStore}",
            new SetActiveSchemaRequestDto(schemaJson));

        var tenantClient = factory.CreateAuthenticatedClient();
        tenantClient.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenantId);

        const int chainLength = 200;
        var chainTuples = Enumerable.Range(0, chainLength)
            .Select(i => new RelationTupleDto(
                new EntityRefDto("doc", $"d-{i}"), "parent", new SubjectRefDto("doc", $"d-{i + 1}", null), null))
            .ToList();
        await tenantClient.PostAsJsonAsync("/api/tuples",
            new WriteTuplesRequestDto(TestAuthHelper.AdminStore, tenantId, "test", chainTuples));

        var req = new CheckRequestDto(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenantId,
            Object: new EntityRefDto("doc", "d-0"),
            Permission: "view",
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: null));

        var resp = await tenantClient.PostAsJsonAsync("/api/check", req);
        resp.StatusCode.ShouldBe((HttpStatusCode)422);

        var warnings = capture.Entries
            .Where(e => e.Category == ProblemDetailsCategory && e.Level == LogLevel.Warning)
            .ToArray();

        warnings.ShouldNotBeEmpty();
        warnings.ShouldContain(e => e.Message.Contains("422") && e.Message.Contains(tenantId));
    }
}
