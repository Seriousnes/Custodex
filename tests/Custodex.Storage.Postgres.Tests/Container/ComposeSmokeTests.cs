using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Container;

[Trait("category", "container")]
public sealed class ComposeSmokeTests
{
    private const string AdminKey = "admin-key";
    private const string Store = "default";
    private const string ServiceUrl = "http://localhost:8080";

    [Fact]
    public async Task Running_container_answers_direct_grant_check()
    {
        using var client = new HttpClient { BaseAddress = new Uri(ServiceUrl) };
        client.DefaultRequestHeaders.Add("X-Custodex-Key", AdminKey);

        var storeResp = await client.PostAsJsonAsync("/v1/stores", new { id = Store });
        (storeResp.StatusCode == HttpStatusCode.Created || storeResp.StatusCode == HttpStatusCode.Conflict)
            .ShouldBeTrue($"create store: {storeResp.StatusCode}");

        var tenantId = $"t-{Guid.NewGuid():N}";

        var tenantResp = await client.PostAsJsonAsync("/v1/tenants", new { storeId = Store, tenantId });
        tenantResp.IsSuccessStatusCode.ShouldBeTrue($"create tenant: {tenantResp.StatusCode}");

        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenantId);

        var schemaResp = await client.PutAsJsonAsync($"/v1/stores/{Store}/schema", new
        {
            schema = """
                schema "v1" {
                    type res {
                        relation owner: user
                        permission read = owner
                    }
                    type user {}
                }
                """
        });
        schemaResp.IsSuccessStatusCode.ShouldBeTrue($"set schema: {schemaResp.StatusCode}");

        var objId = $"obj-{Guid.NewGuid():N}";
        var userId = $"u-{Guid.NewGuid():N}";

        var tuplesResp = await client.PostAsJsonAsync("/v1/tuples", new
        {
            storeId = Store,
            tenantId,
            changeId = "c1",
            tuples = new[]
            {
                new
                {
                    @object = new { type = "res", id = objId },
                    relation = "owner",
                    subject = new { type = "user", id = userId, relation = (string?)null }
                }
            }
        });
        tuplesResp.IsSuccessStatusCode.ShouldBeTrue($"write tuples: {tuplesResp.StatusCode}");

        var checkResp = await client.PostAsJsonAsync("/v1/check", new
        {
            @object = new { type = "res", id = objId },
            permission = "read",
            context = new
            {
                subject = new { type = "user", id = userId },
                attributes = new { }
            }
        });
        checkResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await checkResp.Content.ReadFromJsonAsync<CheckResult>();
        result!.Allowed.ShouldBeTrue();
    }

    private sealed record CheckResult(bool Allowed);
}
