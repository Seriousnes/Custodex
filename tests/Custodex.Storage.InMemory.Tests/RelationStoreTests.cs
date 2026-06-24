using Custodex.Abstractions;
using Shouldly;

namespace Custodex.Storage.InMemory.Tests;

public class RelationStoreTests
{
    private static readonly TenantContext T1 = new("zoo", "t1");
    private static readonly TenantContext T2 = new("zoo", "t2");
    private static readonly NoOpUnitOfWork Uow = new();

    private static RelationTuple Member(string user) =>
        new(new EntityRef("group", "vets"), "member", new SubjectRef("user", user));

    [Fact]
    public async Task Written_tuples_are_returned_by_object_and_relation()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith"), Member("alice")], [], Uow);

        var found = await store.GetByObjectAsync(T1, new EntityRef("group", "vets"), "member");

        found.Select(t => t.Subject.Id).OrderBy(x => x).ShouldBe(["alice", "dr-smith"]);
    }

    [Fact]
    public async Task Remove_matches_on_identity_ignoring_condition_parameter_identity()
    {
        var store = new InMemoryRelationStore();
        var add = new RelationTuple(new EntityRef("category", "drugs"), "dispenser",
            new SubjectRef("group", "vets", "member"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));
        await store.WriteAsync(T1, [add], [], Uow);

        // A logically-equal tuple with a *different dictionary instance* must still remove it.
        var remove = new RelationTuple(new EntityRef("category", "drugs"), "dispenser",
            new SubjectRef("group", "vets", "member"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));
        await store.WriteAsync(T1, [], [remove], Uow);

        (await store.GetByObjectAsync(T1, new EntityRef("category", "drugs"), "dispenser")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_by_subject_returns_tuples_where_the_subject_appears()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);

        var bySubject = await store.GetBySubjectAsync(T1, new SubjectRef("user", "dr-smith"));

        bySubject.ShouldHaveSingleItem().Object.ShouldBe(new EntityRef("group", "vets"));
    }

    [Fact]
    public async Task Tuples_do_not_leak_across_tenants()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);

        (await store.GetByObjectAsync(T2, new EntityRef("group", "vets"), "member")).ShouldBeEmpty();
        (await store.GetBySubjectAsync(T2, new SubjectRef("user", "dr-smith"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Writing_a_duplicate_tuple_is_idempotent()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);

        (await store.GetByObjectAsync(T1, new EntityRef("group", "vets"), "member")).Count.ShouldBe(1);
    }
}
