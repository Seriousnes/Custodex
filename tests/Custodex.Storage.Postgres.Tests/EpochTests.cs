using Custodex.Abstractions;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class EpochTests(PostgresFixture fx) : IAsyncLifetime
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
    public async Task Unwritten_tenant_reads_epoch_zero()
    {
        var t = new TenantContext("store-a", "epoch-fresh");
        await SeedTenantAsync(t);
        var cache = new PostgresCacheStore(fx.ConnectionString, t);
        (await cache.GetEpochAsync(t)).ShouldBe(0);
    }

    [Fact]
    public async Task Bump_increments_and_is_visible_after_commit()
    {
        var t = new TenantContext("store-a", "epoch-bump");
        await SeedTenantAsync(t);
        var cache = new PostgresCacheStore(fx.ConnectionString, t);

        await using (var u = await _factory.BeginAsync())
        {
            await cache.BumpEpochAsync(t, u);
            await cache.BumpEpochAsync(t, u);
            await u.CommitAsync();
        }

        (await cache.GetEpochAsync(t)).ShouldBe(2);
    }

    [Fact]
    public async Task Bump_rolls_back_with_the_unit_of_work()
    {
        var t = new TenantContext("store-a", "epoch-rollback");
        await SeedTenantAsync(t);
        var cache = new PostgresCacheStore(fx.ConnectionString, t);

        await using (var u = await _factory.BeginAsync())
        {
            await cache.BumpEpochAsync(t, u);
        }

        (await cache.GetEpochAsync(t)).ShouldBe(0);
    }
}
