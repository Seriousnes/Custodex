using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conformance;

public class CaseFormatTests
{
    [Fact]
    public void Case_carries_schema_tuples_query_and_expectation()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var viewer = world.Relation();
        var view = world.Permission();
        var objId = world.ObjectId();
        var subjectId = world.SubjectId();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t.Relation(viewer, s => s.Type(world.UserType)).Permission(view, p => p.Relation(viewer)))
            .Build();
        var c = new ConformanceCase(
            Name: "direct grant",
            Schema: schema,
            Tuples: [TestWorld.Tuple(objType, objId, viewer, world.User(subjectId))],
            Attributes: [],
            Object: TestWorld.Object(objType, objId),
            Permission: view,
            Subject: world.User(subjectId),
            Now: DateTimeOffset.UnixEpoch,
            Context: new Dictionary<string, object?>(),
            Expected: true);

        c.Name.ShouldBe("direct grant");
        c.Expected.ShouldBeTrue();
        c.Tuples.Count.ShouldBe(1);
    }
}
