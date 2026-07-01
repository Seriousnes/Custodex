using Custodex.Core.Caching;

using Shouldly;

namespace Custodex.Core.Tests.Caching;

public class CustodexCacheOptionsTests
{
    [Fact]
    public void Options_carry_the_documented_defaults()
    {
        var options = new CustodexCacheOptions();

        options.Enabled.ShouldBeTrue();
        options.Ttl.ShouldBe(TimeSpan.FromMinutes(2));
        options.Conditioned.ShouldBe(ConditionedCaching.Skip);
        options.EpochRefreshInterval.ShouldBe(TimeSpan.FromSeconds(5));
    }
}
