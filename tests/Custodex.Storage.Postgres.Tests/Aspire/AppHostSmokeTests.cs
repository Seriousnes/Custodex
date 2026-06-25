using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Aspire;

[Trait("category", "aspire")]
public sealed class AppHostSmokeTests
{
    private const string AdminKey = "smoke-admin-key";
    private const string Store = "smoke-store";

    [Fact]
    public async Task Orchestrated_stack_answers_direct_grant_check()
    {
        await using var app = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Custodex_AppHost>(
            [],
            (_, hostSettings) =>
            {
                hostSettings.Configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
                hostSettings.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Custodex:TestAdminKey"] = AdminKey,
                    ["Custodex:TestAdminStore"] = Store,
                });
            });

        var builtApp = await app.BuildAsync();
        await builtApp.StartAsync();

        var client = builtApp.CreateHttpClient("custodex-service");
        client.DefaultRequestHeaders.Add("X-Custodex-Key", AdminKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await WaitForHealthyAsync(client, cts.Token);

        var storeResp = await client.PostAsJsonAsync("/v1/stores", new { store = Store });
        storeResp.IsSuccessStatusCode.ShouldBeTrue($"create store: {storeResp.StatusCode}");

        var tenantId = $"t-{Guid.NewGuid():N}";
        var tenantResp = await client.PostAsJsonAsync("/v1/tenants", new { store = Store, tenant = tenantId });
        tenantResp.IsSuccessStatusCode.ShouldBeTrue($"create tenant: {tenantResp.StatusCode}");

        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenantId);

        var schemaResp = await client.PutAsJsonAsync($"/v1/schema/{Store}", new
        {
            schemaJson = """
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
            store = Store,
            tenant = tenantId,
            actor = "smoke",
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

    private static async Task WaitForHealthyAsync(HttpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await client.GetAsync("/health", ct);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        ct.ThrowIfCancellationRequested();
    }

    private sealed record CheckResult(bool Allowed);
}
