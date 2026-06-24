using Custodex.Abstractions;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class CacheEntriesTests(PostgresFixture fx) : IAsyncLifetime
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

    [Fact]
    public async Task Set_then_get_returns_value_and_epoch_stamp()
    {
        var t = new TenantContext("store-a", "cache-rt");
        await SeedTenantAsync(t);
        var cache = new PostgresCacheStore(fx.ConnectionString, t);

        await cache.SetAsync("check:asset:r1:edit:user:alice",
            new CacheEntry([1, 0, 1], Epoch: 7), TimeSpan.FromMinutes(5));

        var got = await cache.GetAsync("check:asset:r1:edit:user:alice");
        got.ShouldNotBeNull();
        got!.Epoch.ShouldBe(7);
        got.Value.ShouldBe(new byte[] { 1, 0, 1 });
    }

    [Fact]
    public async Task Get_returns_null_on_miss()
    {
        var t = new TenantContext("store-a", "cache-miss");
        await SeedTenantAsync(t);
        var cache = new PostgresCacheStore(fx.ConnectionString, t);
        (await cache.GetAsync("nope")).ShouldBeNull();
    }

    [Fact]
    public async Task Expired_entry_reads_as_a_miss()
    {
        var t = new TenantContext("store-a", "cache-expiry");
        await SeedTenantAsync(t);
        var cache = new PostgresCacheStore(fx.ConnectionString, t);

        await cache.SetAsync("k", new CacheEntry([9], Epoch: 1), TimeSpan.FromMilliseconds(-1));
        (await cache.GetAsync("k")).ShouldBeNull();
    }
}
