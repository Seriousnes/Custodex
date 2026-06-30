using Custodex.Core.Conditions;
using Custodex.TestKit;
using Custodex.TestKit.Conformance;

namespace Custodex.Core.Tests.Conformance;

public class EngineConformanceTests
{
    public static IEnumerable<object[]> Corpus() =>
        ConformanceCorpus.All.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Engine_driven_authorizer_satisfies_conformance_scenario(string scenarioName)
    {
        var scenario = ConformanceCorpus.ByName(scenarioName);
        var world = TestWorld.New();
        var attributes = scenario.Attributes.Select(a => (a.Object, a.Attributes)).ToList();

        var authorizer = await world.BuildAsync(
            scenario.Schema, new CelConditionEvaluator(), scenario.Tuples, attributes);

        await ConformanceExecutor.AssertAsync(authorizer, world.Tenant, scenario);
    }
}
