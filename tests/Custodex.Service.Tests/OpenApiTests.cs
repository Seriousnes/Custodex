using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Custodex.Service.Tests;

public sealed class OpenApiTests
{
    private static readonly string[] ExpectedPaths =
    [
        "/api/check",
        "/api/batch-check",
        "/api/list-objects",
        "/api/list-subjects",
        "/api/tuples",
        "/api/attributes",
        "/api/tuples/query",
        "/api/change-log/query",
        "/api/schema/validate",
        "/api/schema/{store}",
        "/api/stores",
        "/api/tenants",
    ];

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Custodex:ConnectionString", "Host=localhost;Database=custodex;Username=custodex;Password=custodex");
            b.UseSetting("Custodex:ApplyMigrationsOnStartup", "false");
        });

    private static async Task<JsonDocument> FetchDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    [Fact]
    public async Task Document_route_returns_200_json_in_production_without_authentication()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        doc.RootElement.GetProperty("openapi").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Document_declares_the_api_title()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        doc.RootElement.GetProperty("info").GetProperty("title").GetString().ShouldBe("Custodex Authorization API");
    }

    [Fact]
    public async Task Document_contains_every_rest_path()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        var paths = doc.RootElement.GetProperty("paths");
        var actual = paths.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in ExpectedPaths)
            actual.ShouldContain(expected);
    }

    [Fact]
    public async Task Document_contains_both_operations_sharing_a_path()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        var paths = doc.RootElement.GetProperty("paths");
        paths.GetProperty("/api/tuples").TryGetProperty("post", out _).ShouldBeTrue();
        paths.GetProperty("/api/tuples").TryGetProperty("delete", out _).ShouldBeTrue();
        paths.GetProperty("/api/schema/{store}").TryGetProperty("put", out _).ShouldBeTrue();
        paths.GetProperty("/api/schema/{store}").TryGetProperty("get", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Document_declares_apikey_and_bearer_security_schemes()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        var schemes = doc.RootElement.GetProperty("components").GetProperty("securitySchemes");

        var apiKey = schemes.GetProperty("ApiKey");
        apiKey.GetProperty("type").GetString().ShouldBe("apiKey");
        apiKey.GetProperty("in").GetString().ShouldBe("header");
        apiKey.GetProperty("name").GetString().ShouldBe("X-Custodex-Key");

        var bearer = schemes.GetProperty("Bearer");
        bearer.GetProperty("type").GetString().ShouldBe("http");
        bearer.GetProperty("scheme").GetString().ShouldBe("bearer");
    }

    [Fact]
    public async Task A_decision_operation_carries_the_security_requirement()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        var checkOperation = doc.RootElement.GetProperty("paths").GetProperty("/api/check").GetProperty("post");
        checkOperation.GetProperty("security").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Sampled_endpoints_have_stable_operation_ids()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        var paths = doc.RootElement.GetProperty("paths");
        paths.GetProperty("/api/check").GetProperty("post").GetProperty("operationId").GetString().ShouldBe("Check");
        paths.GetProperty("/api/stores").GetProperty("post").GetProperty("operationId").GetString().ShouldBe("CreateStore");
    }

    [Fact]
    public async Task A_dto_schema_description_is_sourced_from_xml_docs()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var doc = await FetchDocumentAsync(client);

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var checkRequest = schemas.GetProperty("CheckRequestDto");
        checkRequest.GetProperty("description").GetString().ShouldBe("Request body for a single authorization check.");
    }
}
