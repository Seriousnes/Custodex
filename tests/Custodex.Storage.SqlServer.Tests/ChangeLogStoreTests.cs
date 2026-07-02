using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

[Collection("sqlserver")]
public class ChangeLogStoreTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);

    private static ChangeLogEntry Entry(string actor, string op, string target, object? before, object? after) =>
        new(0, actor, op, target, before, after, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Append_then_read_returns_the_entry_with_db_generated_id()
    {
        var t = new TenantContext("log-s3", "tLog");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerChangeLogStore(fx.ConnectionString);

        await using (var u = await Factory.BeginAsync())
        {
            await store.AppendAsync(t, Entry("admin", "write",
                "folder:item-1#writer@group:reds#member", null, new { granted = true }), u);
            await u.CommitAsync();
        }

        var entries = await store.ReadAsync(t, new ChangeLogFilter());
        var e = entries.ShouldHaveSingleItem();
        e.Id.ShouldBeGreaterThan(0);
        e.Actor.ShouldBe("admin");
        e.Operation.ShouldBe("write");
        e.OccurredAt.ShouldNotBe(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Append_and_bump_return_the_db_assigned_id_and_epoch()
    {
        var t = new TenantContext("log-s3", "tReturns");
        await Seed.TenantAsync(Factory, t);
        var log = new SqlServerChangeLogStore(fx.ConnectionString);
        var cache = new SqlServerCacheStore(fx.ConnectionString, t);

        long id1, id2, epoch1, epoch2;
        await using (var u = await Factory.BeginAsync())
        {
            id1 = await log.AppendAsync(t, Entry("admin", "write", "a#r@u", null, null), u);
            epoch1 = await cache.BumpEpochAsync(t, u);
            id2 = await log.AppendAsync(t, Entry("admin", "write", "b#r@u", null, null), u);
            epoch2 = await cache.BumpEpochAsync(t, u);
            await u.CommitAsync();
        }

        id1.ShouldBeGreaterThan(0);
        id2.ShouldBeGreaterThan(id1);
        epoch1.ShouldBe(1);
        epoch2.ShouldBe(2);
    }

    [Fact]
    public async Task Read_filters_by_actor_and_respects_limit_newest_first()
    {
        var t = new TenantContext("log-s3", "tLog2");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerChangeLogStore(fx.ConnectionString);

        await using (var u = await Factory.BeginAsync())
        {
            await store.AppendAsync(t, Entry("alice", "write", "a#r@u", null, null), u);
            await store.AppendAsync(t, Entry("bob", "delete", "b#r@u", null, null), u);
            await store.AppendAsync(t, Entry("alice", "write", "c#r@u", null, null), u);
            await u.CommitAsync();
        }

        var aliceOnly = await store.ReadAsync(t, new ChangeLogFilter(Actor: "alice"));
        aliceOnly.Count.ShouldBe(2);
        aliceOnly.All(e => e.Actor == "alice").ShouldBeTrue();

        var capped = await store.ReadAsync(t, new ChangeLogFilter(Limit: 1));
        capped.Count.ShouldBe(1);
        capped[0].Target.ShouldBe("c#r@u");
    }
}
