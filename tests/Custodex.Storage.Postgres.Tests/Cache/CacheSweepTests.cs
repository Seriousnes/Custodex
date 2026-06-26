using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class CacheSweepTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private readonly TenantContext _t = new("s1", "sweep");

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "DELETE FROM cache_entries WHERE store_id = @s AND tenant_id = @t",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> RowCountAsync()
    {
        await using var conn = await fx.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM cache_entries WHERE store_id = @s AND tenant_id = @t",
            new { s = _t.Store, t = _t.Tenant });
    }

    [Fact]
    public async Task Sweep_removes_expired_rows_and_keeps_live_ones()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        await cache.SetAsync("live", new CacheEntry([1], Epoch: 1), TimeSpan.FromMinutes(10));
        await cache.SetAsync("dead", new CacheEntry([2], Epoch: 1), TimeSpan.FromMilliseconds(-1));

        (await RowCountAsync()).ShouldBe(2);

        var reclaimed = await CacheSweep.RunAsync(fx.ConnectionString);
        reclaimed.ShouldBeGreaterThanOrEqualTo(1);

        (await RowCountAsync()).ShouldBe(1);
        (await cache.GetAsync("live")).ShouldNotBeNull();
        (await cache.GetAsync("dead")).ShouldBeNull();
    }

    [Fact]
    public async Task Sweep_on_a_clean_table_reclaims_nothing()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        await cache.SetAsync("only-live", new CacheEntry([3], Epoch: 1), TimeSpan.FromMinutes(10));

        var before = await RowCountAsync();
        await CacheSweep.RunAsync(fx.ConnectionString);
        (await RowCountAsync()).ShouldBe(before);
    }
}
