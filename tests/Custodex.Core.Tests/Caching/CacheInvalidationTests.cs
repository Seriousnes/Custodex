using Custodex.Abstractions;
using Custodex.Core.Caching;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Caching;

public class CacheInvalidationTests
{
    [Fact]
    public async Task OnWrite_bumps_the_epoch_so_old_entries_are_stale()
    {
        var world = TestWorld.New();
        var cache = new InMemoryCacheStore();
        var uow = new NoOpUnitOfWork();

        var before = await cache.GetEpochAsync(world.Tenant);
        await cache.SetAsync("k", new CacheEntry([1], before), TimeSpan.FromMinutes(5));

        await CacheInvalidation.OnWriteAsync(cache, world.Tenant, uow);
        await uow.CommitAsync();

        var after = await cache.GetEpochAsync(world.Tenant);
        after.ShouldNotBe(before);

        var entry = await cache.GetAsync("k");
        entry.ShouldNotBeNull();
        (entry!.Epoch == after).ShouldBeFalse();
    }
}
