using Custodex.Abstractions;
using Custodex.Core.Caching;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class PostgresCacheCodecTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private readonly TenantContext _t = new("s1", "codec");

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

    [Fact]
    public async Task Bool_decision_round_trips_through_the_store_via_the_codec()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        var epoch = await cache.GetEpochAsync(_t);

        await cache.SetAsync("check:doc:D1:view:user:alice",
            new CacheEntry(CacheValueCodec.Encode(true), epoch), TimeSpan.FromMinutes(5));

        var entry = await cache.GetAsync("check:doc:D1:view:user:alice");
        entry.ShouldNotBeNull();
        CacheValueCodec.Decode(entry!.Value).ShouldBeTrue();
    }

    [Fact]
    public async Task Epoch_mismatch_is_treated_as_a_miss_by_the_caching_layer()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        var epoch = await cache.GetEpochAsync(_t);

        await cache.SetAsync("k", new CacheEntry(CacheValueCodec.Encode(false), epoch), TimeSpan.FromMinutes(5));

        await using (var u = await _factory.BeginAsync())
        {
            await cache.BumpEpochAsync(_t, u);
            await u.CommitAsync();
        }

        var current = await cache.GetEpochAsync(_t);
        var entry = await cache.GetAsync("k");

        entry.ShouldNotBeNull();
        (entry!.Epoch == current).ShouldBeFalse();
    }
}
