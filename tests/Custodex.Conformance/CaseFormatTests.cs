using Custodex.Abstractions;
using Shouldly;

namespace Custodex.Conformance;

public class CaseFormatTests
{
    [Fact]
    public void Case_carries_schema_tuples_query_and_expectation()
    {
        var schema = new Custodex.Core.SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();
        var c = new ConformanceCase(
            Name: "direct grant",
            Schema: schema,
            Tuples: new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")) },
            Attributes: Array.Empty<AttributeSeed>(),
            Object: new EntityRef("doc", "D1"),
            Permission: "view",
            Subject: new SubjectRef("user", "alice"),
            Now: DateTimeOffset.UnixEpoch,
            Context: new Dictionary<string, object?>(),
            Expected: true);

        c.Name.ShouldBe("direct grant");
        c.Expected.ShouldBeTrue();
        c.Tuples.Count.ShouldBe(1);
    }
}
