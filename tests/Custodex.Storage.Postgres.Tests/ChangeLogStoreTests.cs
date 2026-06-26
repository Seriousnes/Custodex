using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class ChangeLogStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    private static ChangeLogEntry Entry(string actor, string op, string target, object? before, object? after) =>
        new(0, actor, op, target, before, after, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Append_then_read_returns_the_entry_with_db_generated_id()
    {
        var t = new TenantContext("s3", "tLog");
        await SeedTenantAsync(t);
        var store = new NpgsqlChangeLogStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
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
    public async Task Read_filters_by_actor_and_respects_limit_newest_first()
    {
        var t = new TenantContext("s3", "tLog2");
        await SeedTenantAsync(t);
        var store = new NpgsqlChangeLogStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
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
