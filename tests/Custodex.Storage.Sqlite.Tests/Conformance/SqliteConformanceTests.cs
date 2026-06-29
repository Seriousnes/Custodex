using Custodex.Abstractions;
using Custodex.Core;
using Custodex.TestKit.Conformance;

using Microsoft.Extensions.DependencyInjection;

namespace Custodex.Storage.Sqlite.Tests.Conformance;

public class SqliteConformanceTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    public static IEnumerable<object[]> Corpus() =>
        ConformanceCorpus.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Engine_over_sqlite_satisfies_conformance_scenario(string scenarioName)
    {
        var scenario = ConformanceCorpus.ByName(scenarioName);
        var store = $"cf-{scenarioName}";
        var tenant = new TenantContext(store, "t");

        var services = new ServiceCollection();
        services.AddCustodex().UseSqlite(fx.ConnectionString);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IStoreManager>().CreateStoreAsync(store);
        await provider.GetRequiredService<ITenantManager>().CreateTenantAsync(tenant);
        await provider.GetRequiredService<ITenantManager>().CreateTenantAsync(new TenantContext(store, store));
        await provider.GetRequiredService<ISchemaManager>().SetActiveSchemaAsync(store, scenario.Schema);

        var relations = provider.GetRequiredService<IRelationManager>();
        if (scenario.Tuples.Count > 0)
            await relations.WriteTuplesAsync(tenant, "conformance", scenario.Tuples);
        foreach (var seed in scenario.Attributes)
            await relations.WriteAttributesAsync(tenant, "conformance", seed.Object, seed.Attributes);

        var authorizer = provider.GetRequiredService<IAuthorizer>();
        await ConformanceExecutor.AssertAsync(authorizer, tenant, scenario);
    }
}
