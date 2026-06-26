using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Storage.InMemory.Tests;

public class RelationStoreTests
{
    private static readonly NoOpUnitOfWork Uow = new();

    [Fact]
    public async Task Written_tuples_are_returned_by_object_and_relation()
    {
        var world = TestWorld.New();
        var tenant = world.Tenant;
        var groupId = world.ObjectId();
        var obj = TestWorld.Object(world.GroupType, groupId);
        var subject1 = world.SubjectId();
        var subject2 = world.SubjectId();
        RelationTuple Member(string user) =>
            TestWorld.Tuple(world.GroupType, groupId, world.MemberRelation, world.User(user));
        var store = new InMemoryRelationStore();
        await store.WriteAsync(tenant, [Member(subject1), Member(subject2)], [], Uow);

        var found = await store.GetByObjectAsync(tenant, obj, world.MemberRelation);

        found.Select(t => t.Subject.Id).OrderBy(x => x)
            .ShouldBe(new[] { subject1, subject2 }.OrderBy(x => x));
    }

    [Fact]
    public async Task Remove_matches_on_identity_ignoring_condition_parameter_identity()
    {
        var world = TestWorld.New();
        var tenant = world.Tenant;
        var objType = world.EntityType();
        var objId = world.ObjectId();
        var relation = world.Relation();
        var groupId = world.ObjectId();
        var conditionName = world.ConditionName();
        var startParam = world.ParamName();
        var endParam = world.ParamName();
        var store = new InMemoryRelationStore();
        var add = new RelationTuple(new EntityRef(objType, objId), relation,
            new SubjectRef(world.GroupType, groupId, world.MemberRelation),
            new ConditionRef(conditionName, new Dictionary<string, object?> { [startParam] = 8, [endParam] = 18 }));
        await store.WriteAsync(tenant, [add], [], Uow);

        var remove = new RelationTuple(new EntityRef(objType, objId), relation,
            new SubjectRef(world.GroupType, groupId, world.MemberRelation),
            new ConditionRef(conditionName, new Dictionary<string, object?> { [startParam] = 8, [endParam] = 18 }));
        await store.WriteAsync(tenant, [], [remove], Uow);

        (await store.GetByObjectAsync(tenant, new EntityRef(objType, objId), relation)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_by_subject_returns_tuples_where_the_subject_appears()
    {
        var world = TestWorld.New();
        var tenant = world.Tenant;
        var groupId = world.ObjectId();
        var obj = TestWorld.Object(world.GroupType, groupId);
        var subject = world.SubjectId();
        var store = new InMemoryRelationStore();
        await store.WriteAsync(tenant,
            [TestWorld.Tuple(world.GroupType, groupId, world.MemberRelation, world.User(subject))], [], Uow);

        var bySubject = await store.GetBySubjectAsync(tenant, world.User(subject));

        bySubject.ShouldHaveSingleItem().Object.ShouldBe(obj);
    }

    [Fact]
    public async Task Tuples_do_not_leak_across_tenants()
    {
        var world = TestWorld.New();
        var t1 = world.Tenant;
        var t2 = new TenantContext(world.Tenant.Store, world.EntityType());
        var groupId = world.ObjectId();
        var obj = TestWorld.Object(world.GroupType, groupId);
        var subject = world.SubjectId();
        var store = new InMemoryRelationStore();
        await store.WriteAsync(t1,
            [TestWorld.Tuple(world.GroupType, groupId, world.MemberRelation, world.User(subject))], [], Uow);

        (await store.GetByObjectAsync(t2, obj, world.MemberRelation)).ShouldBeEmpty();
        (await store.GetBySubjectAsync(t2, world.User(subject))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Writing_a_duplicate_tuple_is_idempotent()
    {
        var world = TestWorld.New();
        var tenant = world.Tenant;
        var groupId = world.ObjectId();
        var obj = TestWorld.Object(world.GroupType, groupId);
        var subject = world.SubjectId();
        RelationTuple Member() =>
            TestWorld.Tuple(world.GroupType, groupId, world.MemberRelation, world.User(subject));
        var store = new InMemoryRelationStore();
        await store.WriteAsync(tenant, [Member()], [], Uow);
        await store.WriteAsync(tenant, [Member()], [], Uow);

        (await store.GetByObjectAsync(tenant, obj, world.MemberRelation)).Count.ShouldBe(1);
    }
}
