using Custodex.Abstractions;
using Custodex.Storage.Postgres.Tests.Differential;
using Custodex.TestKit.Conformance;

namespace Custodex.Storage.Postgres.Tests.Conformance;

[Collection("postgres")]
public class PostgresConformanceTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static IEnumerable<object[]> Corpus() =>
        ConformanceCorpus.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Cte_authorizer_satisfies_conformance_scenario(string scenarioName)
    {
        var scenario = ConformanceCorpus.ByName(scenarioName);
        var store = $"cf-{scenarioName}";
        var attributes = scenario.Attributes.Select(a => (a.Object, a.Attributes)).ToList();
        var model = new GeneratedModel(scenario.Schema, scenario.Tuples, attributes, [], []);

        var (_, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
        var tenant = new TenantContext(store, "t");

        await ConformanceExecutor.AssertAsync(cte, tenant, scenario);
    }
}
