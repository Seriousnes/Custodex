using Custodex.Abstractions;
using Shouldly;

namespace Custodex.Storage.InMemory.Tests;

public class ChangeLogStoreTests
{
    private static readonly TenantContext T1 = new("zoo", "t1");
    private static readonly TenantContext T2 = new("zoo", "t2");
    private static readonly NoOpUnitOfWork Uow = new();

    private static ChangeLogEntry Entry(string actor, DateTimeOffset at) =>
        new(0, actor, "write", "category:drugs#dispenser@group:vets#member", null, null, at);

    [Fact]
    public async Task Append_assigns_sequential_ids_starting_at_one()
    {
        var store = new InMemoryChangeLogStore();
        await store.AppendAsync(T1, Entry("alice", DateTimeOffset.UnixEpoch), Uow);
        await store.AppendAsync(T1, Entry("bob", DateTimeOffset.UnixEpoch.AddSeconds(1)), Uow);

        var entries = await store.ReadAsync(T1, new ChangeLogFilter());

        entries.Select(e => e.Id).ShouldBe([2, 1]);   // newest first
        entries[0].Actor.ShouldBe("bob");
    }

    [Fact]
    public async Task Read_filters_by_actor_and_since_and_limit()
    {
        var store = new InMemoryChangeLogStore();
        var t0 = DateTimeOffset.UnixEpoch;
        await store.AppendAsync(T1, Entry("alice", t0), Uow);
        await store.AppendAsync(T1, Entry("bob", t0.AddMinutes(5)), Uow);
        await store.AppendAsync(T1, Entry("bob", t0.AddMinutes(10)), Uow);

        var byActor = await store.ReadAsync(T1, new ChangeLogFilter(Actor: "bob"));
        byActor.Count.ShouldBe(2);
        byActor.ShouldAllBe(e => e.Actor == "bob");

        var since = await store.ReadAsync(T1, new ChangeLogFilter(Since: t0.AddMinutes(6)));
        since.ShouldHaveSingleItem().OccurredAt.ShouldBe(t0.AddMinutes(10));

        var capped = await store.ReadAsync(T1, new ChangeLogFilter(Limit: 1));
        capped.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Change_log_does_not_leak_across_tenants()
    {
        var store = new InMemoryChangeLogStore();
        await store.AppendAsync(T1, Entry("alice", DateTimeOffset.UnixEpoch), Uow);

        (await store.ReadAsync(T2, new ChangeLogFilter())).ShouldBeEmpty();
    }
}
