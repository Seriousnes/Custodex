using Custodex.Abstractions;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Storage.InMemory.Tests;

public class CacheStoreTests
{
    private static readonly NoOpUnitOfWork Uow = new();

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task Set_then_get_round_trips_value_and_epoch()
    {
        var store = new InMemoryCacheStore(new TestClock(DateTimeOffset.UnixEpoch));
        await store.SetAsync("k", new CacheEntry([1, 2, 3], Epoch: 7), TimeSpan.FromMinutes(5));

        var entry = await store.GetAsync("k");

        entry!.Value.ShouldBe([1, 2, 3]);
        entry.Epoch.ShouldBe(7);
    }

    [Fact]
    public async Task Missing_key_returns_null()
    {
        (await new InMemoryCacheStore().GetAsync("absent")).ShouldBeNull();
    }

    [Fact]
    public async Task Entry_expires_after_its_ttl()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryCacheStore(clock);
        await store.SetAsync("k", new CacheEntry([9], 1), TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(4));
        (await store.GetAsync("k")).ShouldNotBeNull();

        clock.Advance(TimeSpan.FromMinutes(2));   // now 6 minutes > 5-minute TTL
        (await store.GetAsync("k")).ShouldBeNull();
    }

    [Fact]
    public async Task Epoch_starts_at_zero_and_increments_on_bump()
    {
        var world = TestWorld.New();
        var tenant = world.Tenant;
        var store = new InMemoryCacheStore();

        (await store.GetEpochAsync(tenant)).ShouldBe(0);
        await store.BumpEpochAsync(tenant, Uow);
        await store.BumpEpochAsync(tenant, Uow);
        (await store.GetEpochAsync(tenant)).ShouldBe(2);
    }
}
