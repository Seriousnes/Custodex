using Custodex.Abstractions;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Storage.InMemory.Tests;

public class ChangeLogStoreTests
{
    private static readonly NoOpUnitOfWork Uow = new();

    private static ChangeLogEntry Entry(string target, string actor, DateTimeOffset at) =>
        new(0, actor, "write", target, null, null, at);

    [Fact]
    public async Task Append_assigns_sequential_ids_starting_at_one()
    {
        var world = TestWorld.New();
        var tenant = world.Tenant;
        var target = $"{world.EntityType()}:{world.ObjectId()}#{world.Relation()}";
        var actor1 = world.SubjectId();
        var actor2 = world.SubjectId();
        var store = new InMemoryChangeLogStore();
        await store.AppendAsync(tenant, Entry(target, actor1, DateTimeOffset.UnixEpoch), Uow);
        await store.AppendAsync(tenant, Entry(target, actor2, DateTimeOffset.UnixEpoch.AddSeconds(1)), Uow);

        var entries = await store.ReadAsync(tenant, new ChangeLogFilter());

        entries.Select(e => e.Id).ShouldBe([2, 1]);   // newest first
        entries[0].Actor.ShouldBe(actor2);
    }

    [Fact]
    public async Task Read_filters_by_actor_and_since_and_limit()
    {
        var world = TestWorld.New();
        var tenant = world.Tenant;
        var target = $"{world.EntityType()}:{world.ObjectId()}#{world.Relation()}";
        var actor1 = world.SubjectId();
        var actor2 = world.SubjectId();
        var store = new InMemoryChangeLogStore();
        var t0 = DateTimeOffset.UnixEpoch;
        await store.AppendAsync(tenant, Entry(target, actor1, t0), Uow);
        await store.AppendAsync(tenant, Entry(target, actor2, t0.AddMinutes(5)), Uow);
        await store.AppendAsync(tenant, Entry(target, actor2, t0.AddMinutes(10)), Uow);

        var byActor = await store.ReadAsync(tenant, new ChangeLogFilter(Actor: actor2));
        byActor.Count.ShouldBe(2);
        byActor.ShouldAllBe(e => e.Actor == actor2);

        var since = await store.ReadAsync(tenant, new ChangeLogFilter(Since: t0.AddMinutes(6)));
        since.ShouldHaveSingleItem().OccurredAt.ShouldBe(t0.AddMinutes(10));

        var capped = await store.ReadAsync(tenant, new ChangeLogFilter(Limit: 1));
        capped.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Change_log_does_not_leak_across_tenants()
    {
        var world = TestWorld.New();
        var target = $"{world.EntityType()}:{world.ObjectId()}#{world.Relation()}";
        var actor = world.SubjectId();
        var t1 = world.Tenant;
        var t2 = new TenantContext(world.Tenant.Store, world.EntityType());
        var store = new InMemoryChangeLogStore();
        await store.AppendAsync(t1, Entry(target, actor, DateTimeOffset.UnixEpoch), Uow);

        (await store.ReadAsync(t2, new ChangeLogFilter())).ShouldBeEmpty();
    }
}
