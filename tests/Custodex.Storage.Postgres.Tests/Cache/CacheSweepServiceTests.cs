using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class CacheSweepServiceTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private readonly TenantContext _t = new("s1", "sweep-svc");

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
    public async Task Service_sweeps_expired_rows_on_its_interval()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        await cache.SetAsync("dead", new CacheEntry([1], Epoch: 1), TimeSpan.FromMilliseconds(-1));
        (await RowCountAsync()).ShouldBe(1);

        var options = new CacheSweepOptions
        {
            ConnectionString = fx.ConnectionString,
            Interval = TimeSpan.FromMilliseconds(50)
        };
        var service = new CacheSweepService(options);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await RowCountAsync() > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        await service.StopAsync(cts.Token);
        (await RowCountAsync()).ShouldBe(0);
    }
}
