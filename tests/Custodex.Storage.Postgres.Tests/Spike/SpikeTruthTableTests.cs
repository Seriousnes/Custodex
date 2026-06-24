using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Spike;

public class SpikeTruthTableTests
{
    [Fact]
    public void Case_A_inner_exclusion_truth_is_pinned()
    {
        SpikeData.CaseA_Expected[("D1", "carol")].ShouldBeFalse();
        SpikeData.CaseA_Expected[("D1", "dana")].ShouldBeTrue();
    }

    [Fact]
    public void Case_B_intersection_through_arrow_truth_is_pinned()
    {
        SpikeData.CaseB_Expected[("D1", "pat")].ShouldBeTrue();
        SpikeData.CaseB_Expected[("D1", "jones")].ShouldBeFalse();
        SpikeData.CaseB_Expected[("D1", "outsider")].ShouldBeFalse();
    }
}
