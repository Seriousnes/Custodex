using Relkit.Abstractions;
using Relkit.Core;
using Shouldly;
using Xunit;

namespace Relkit.Conformance;

public class RunnerTests
{
    [Fact]
    public async Task Runs_a_simple_allow_case()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();
        var c = new ConformanceCase("allow", schema,
            new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")) },
            Array.Empty<AttributeSeed>(),
            new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);

        var result = await ConformanceRunner.RunAsync(c);
        result.Allowed.ShouldBeTrue();
        await ConformanceRunner.AssertAsync(c);   // should not throw
    }

    [Fact]
    public async Task Runs_a_simple_deny_case()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();
        var c = new ConformanceCase("deny", schema,
            Array.Empty<RelationTuple>(), Array.Empty<AttributeSeed>(),
            new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: false);

        await ConformanceRunner.AssertAsync(c);
    }
}
