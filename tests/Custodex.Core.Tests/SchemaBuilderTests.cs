using Custodex.Abstractions;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests;

public class SchemaBuilderTests
{
    [Fact]
    public void Builds_a_schema_with_relations_permission_and_condition()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var linkedType = world.EntityType();
        var grant = world.Relation();
        var link = world.Relation();
        var blocked = world.Relation();
        var edit = world.Permission();
        var condition = world.ConditionName();
        var start = world.ParamName();
        var end = world.ParamName();

        var schema = new SchemaBuilder(world.Version)
            .Type(world.GroupType, t => t.Relation(world.MemberRelation,
                s => s.Type(world.UserType).SubjectSet(world.GroupType, world.MemberRelation)))
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType).SubjectSet(world.GroupType, world.MemberRelation))
                .Relation(link, s => s.Type(linkedType))
                .Relation(blocked, s => s.Type(world.UserType).SubjectSet(world.GroupType, world.MemberRelation))
                .Permission(edit, p => p.Relation(grant).Arrow(link, edit).Exclude(x => x.Relation(blocked))))
            .Condition(condition, c => c.Int(start).Int(end))
            .Build();

        schema.Version.ShouldBe(world.Version);
        var type = schema.Types.Single(x => x.Name == objType);
        type.Relations.Select(r => r.Name).ShouldBe(new[] { grant, link, blocked });
        type.Permissions.Single().Name.ShouldBe(edit);
        type.Permissions.Single().Expression.ShouldBeOfType<Exclude>();
        schema.Conditions.Single().Name.ShouldBe(condition);
        schema.Conditions.Single().Parameters.Count.ShouldBe(2);
    }
}
