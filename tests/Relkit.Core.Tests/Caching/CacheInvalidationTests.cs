using Relkit.Abstractions;
using Relkit.Core.Caching;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Caching;

public class CacheInvalidationTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    [Fact]
    public async Task OnWrite_bumps_the_epoch_so_old_entries_are_stale()
    {
        var cache = new InMemoryCacheStore();
        var uow = new NoOpUnitOfWork();

        var before = await cache.GetEpochAsync(T);
        await cache.SetAsync("k", new CacheEntry([1], before), TimeSpan.FromMinutes(5));

        await CacheInvalidation.OnWriteAsync(cache, T, uow);
        await uow.CommitAsync();

        var after = await cache.GetEpochAsync(T);
        after.ShouldNotBe(before);

        var entry = await cache.GetAsync("k");
        // The entry still exists physically, but its epoch no longer matches => a reader treats it as a miss.
        entry.ShouldNotBeNull();
        (entry!.Epoch == after).ShouldBeFalse();
    }
}
