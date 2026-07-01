using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ExclusionCycleTests
{
    [Fact]
    public async Task Check_fails_closed_when_the_excluded_branch_cycles_back_through_an_arrow()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var link = world.Relation();
        var view = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(link, s => s.Type(objType))
                .Permission(view, p => p.Relation(grant).Exclude(x => x.Arrow(link, view))))
            .Build();
        var obj = world.ObjectId();
        var user = world.SubjectId();

        var auth = await world.BuildAsync(schema,
            TestWorld.Tuple(objType, obj, grant, world.User(user)),
            TestWorld.Tuple(objType, obj, link, new SubjectRef(objType, obj)));

        await Should.ThrowAsync<ExclusionCycleException>(
            () => auth.CheckAsync(world.Check(objType, obj, view, user)));
    }

    [Fact]
    public async Task Check_fails_closed_from_every_entry_point_of_a_two_object_exclusion_cycle()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var link = world.Relation();
        var view = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(link, s => s.Type(objType))
                .Permission(view, p => p.Relation(grant).Exclude(x => x.Arrow(link, view))))
            .Build();
        var first = world.ObjectId();
        var second = world.ObjectId();
        var user = world.SubjectId();

        var auth = await world.BuildAsync(schema,
            TestWorld.Tuple(objType, first, grant, world.User(user)),
            TestWorld.Tuple(objType, second, grant, world.User(user)),
            TestWorld.Tuple(objType, first, link, new SubjectRef(objType, second)),
            TestWorld.Tuple(objType, second, link, new SubjectRef(objType, first)));

        await Should.ThrowAsync<ExclusionCycleException>(
            () => auth.CheckAsync(world.Check(objType, first, view, user)));
        await Should.ThrowAsync<ExclusionCycleException>(
            () => auth.CheckAsync(world.Check(objType, second, view, user)));
    }

    [Fact]
    public async Task Cyclic_data_inside_a_stratified_excluded_branch_still_evaluates()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var holderType = world.EntityType();
        var grant = world.Relation();
        var mark = world.Relation();
        var home = world.Relation();
        var parent = world.Relation();
        var view = world.Permission();
        var marked = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(holderType, t => t
                .Relation(mark, s => s.Type(world.UserType))
                .Relation(parent, s => s.Type(holderType))
                .Permission(marked, p => p.Relation(mark).Union(x => x.Arrow(parent, marked))))
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(home, s => s.Type(holderType))
                .Permission(view, p => p.Relation(grant).Exclude(x => x.Arrow(home, marked))))
            .Build();
        var obj = world.ObjectId();
        var holderA = world.ObjectId();
        var holderB = world.ObjectId();
        var cleanUser = world.SubjectId();
        var markedUser = world.SubjectId();

        var auth = await world.BuildAsync(schema,
            TestWorld.Tuple(objType, obj, grant, world.User(cleanUser)),
            TestWorld.Tuple(objType, obj, grant, world.User(markedUser)),
            TestWorld.Tuple(objType, obj, home, new SubjectRef(holderType, holderA)),
            TestWorld.Tuple(holderType, holderA, parent, new SubjectRef(holderType, holderB)),
            TestWorld.Tuple(holderType, holderB, parent, new SubjectRef(holderType, holderA)),
            TestWorld.Tuple(holderType, holderB, mark, world.User(markedUser)));

        (await auth.CheckAsync(world.Check(objType, obj, view, cleanUser))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(world.Check(objType, obj, view, markedUser))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Cyclic_group_nesting_inside_an_excluded_relation_still_evaluates()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var blocked = world.Relation();
        var view = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(world.GroupType, t => t
                .Relation(world.MemberRelation,
                    s => s.Type(world.UserType).SubjectSet(world.GroupType, world.MemberRelation)))
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(blocked, s => s.Type(world.UserType).SubjectSet(world.GroupType, world.MemberRelation))
                .Permission(view, p => p.Relation(grant).Exclude(x => x.Relation(blocked))))
            .Build();
        var obj = world.ObjectId();
        var groupA = world.ObjectId();
        var groupB = world.ObjectId();
        var cleanUser = world.SubjectId();
        var blockedUser = world.SubjectId();

        var auth = await world.BuildAsync(schema,
            TestWorld.Tuple(objType, obj, grant, world.User(cleanUser)),
            TestWorld.Tuple(objType, obj, grant, world.User(blockedUser)),
            TestWorld.Tuple(objType, obj, blocked, world.Member(groupA)),
            TestWorld.Tuple(world.GroupType, groupA, world.MemberRelation, world.Member(groupB)),
            TestWorld.Tuple(world.GroupType, groupB, world.MemberRelation, world.Member(groupA)),
            TestWorld.Tuple(world.GroupType, groupB, world.MemberRelation, world.User(blockedUser)));

        (await auth.CheckAsync(world.Check(objType, obj, view, cleanUser))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(world.Check(objType, obj, view, blockedUser))).Allowed.ShouldBeFalse();
    }
}
