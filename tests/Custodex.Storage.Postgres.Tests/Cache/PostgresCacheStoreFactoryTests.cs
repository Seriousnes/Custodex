using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class PostgresCacheStoreFactoryTests(PostgresFixture fx) : IAsyncLifetime
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
    public async Task Two_store_instances_for_the_same_tenant_share_rows()
    {
        var t = new TenantContext("s1", "shared");
        await SeedTenantAsync(t);
        var factory = new PostgresCacheStoreFactory(fx.ConnectionString);

        var writer = factory.For(t);
        var reader = factory.For(t);

        await writer.SetAsync("k", new CacheEntry([7], Epoch: 3), TimeSpan.FromMinutes(5));
        var got = await reader.GetAsync("k");

        got.ShouldNotBeNull();
        got!.Value.ShouldBe([7]);
        got.Epoch.ShouldBe(3);
    }

    [Fact]
    public async Task Different_tenants_are_isolated()
    {
        var a = new TenantContext("s1", "tenant-a");
        var b = new TenantContext("s1", "tenant-b");
        await SeedTenantAsync(a);
        await SeedTenantAsync(b);
        var factory = new PostgresCacheStoreFactory(fx.ConnectionString);

        await factory.For(a).SetAsync("k", new CacheEntry([1], Epoch: 1), TimeSpan.FromMinutes(5));

        (await factory.For(b).GetAsync("k")).ShouldBeNull();
    }
}
