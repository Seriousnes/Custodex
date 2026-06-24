using Custodex.TestKit;
using Shouldly;

namespace Custodex.Abstractions.Tests;

public class ReferenceTypesTests
{
    [Fact]
    public void EntityRef_detects_wildcard_and_formats()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();

        new EntityRef(type, id).IsWildcard.ShouldBeFalse();
        new EntityRef(world.UserType, "*").IsWildcard.ShouldBeTrue();
        new EntityRef(type, id).ToString().ShouldBe($"{type}:{id}");
    }

    [Fact]
    public void SubjectRef_detects_subject_set_and_wildcard()
    {
        var world = TestWorld.New();

        new SubjectRef(world.GroupType, world.ObjectId(), world.MemberRelation).IsSubjectSet.ShouldBeTrue();
        new SubjectRef(world.UserType, world.SubjectId()).IsSubjectSet.ShouldBeFalse();
        new SubjectRef(world.UserType, "*").IsWildcard.ShouldBeTrue();
    }
}
