using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conformance;

public class RunnerTests
{
    [Fact]
    public async Task Runs_a_simple_allow_case()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var viewer = world.Relation();
        var view = world.Permission();
        var objId = world.ObjectId();
        var subjectId = world.SubjectId();

        var schema = new SchemaBuilder(world.Version)
            .Type(objType, t => t.Relation(viewer, s => s.Type(world.UserType)).Permission(view, p => p.Relation(viewer)))
            .Build();
        var c = new ConformanceCase("allow", schema,
            [world.Tuple(objType, objId, viewer, world.User(subjectId))],
            [],
            world.Object(objType, objId), view, world.User(subjectId),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);

        var result = await ConformanceRunner.RunAsync(c);
        result.Allowed.ShouldBeTrue();
        await ConformanceRunner.AssertAsync(c);
    }

    [Fact]
    public async Task Runs_a_simple_deny_case()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var viewer = world.Relation();
        var view = world.Permission();
        var objId = world.ObjectId();
        var subjectId = world.SubjectId();

        var schema = new SchemaBuilder(world.Version)
            .Type(objType, t => t.Relation(viewer, s => s.Type(world.UserType)).Permission(view, p => p.Relation(viewer)))
            .Build();
        var c = new ConformanceCase("deny", schema,
            [], [],
            world.Object(objType, objId), view, world.User(subjectId),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: false);

        await ConformanceRunner.AssertAsync(c);
    }
}
