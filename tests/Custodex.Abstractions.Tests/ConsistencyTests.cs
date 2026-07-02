using Custodex.TestKit;

using Shouldly;

namespace Custodex.Abstractions.Tests;

public class ConsistencyTests
{
    [Fact]
    public void MinimizeLatency_selects_the_latency_mode_with_no_token()
    {
        Consistency.MinimizeLatency.Mode.ShouldBe(ConsistencyMode.MinimizeLatency);
        Consistency.MinimizeLatency.Token.ShouldBeNull();
    }

    [Fact]
    public void FullyConsistent_selects_the_fully_consistent_mode_with_no_token()
    {
        Consistency.FullyConsistent.Mode.ShouldBe(ConsistencyMode.FullyConsistent);
        Consistency.FullyConsistent.Token.ShouldBeNull();
    }

    [Fact]
    public void AtLeastAsFresh_carries_the_required_token()
    {
        var token = ConsistencyToken.Create(TestWorld.New().Tenant, epoch: 4, changeLogId: 8);

        var consistency = Consistency.AtLeastAsFresh(token);

        consistency.Mode.ShouldBe(ConsistencyMode.AtLeastAsFresh);
        consistency.Token.ShouldBe(token);
    }

    [Fact]
    public void RequestContext_defaults_consistency_to_null()
    {
        var world = TestWorld.New();
        var context = new RequestContext(DateTimeOffset.UnixEpoch, world.User("s"), new Dictionary<string, object?>());

        context.Consistency.ShouldBeNull();
    }
}
