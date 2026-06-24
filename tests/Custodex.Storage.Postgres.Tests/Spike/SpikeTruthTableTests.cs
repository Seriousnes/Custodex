using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Spike;

public class SpikeTruthTableTests
{
    [Fact]
    public void Case_A_inner_exclusion_truth_is_pinned()
    {
        SpikeData.CaseA_Expected[("EL-001", "carol")].ShouldBeFalse();
        SpikeData.CaseA_Expected[("EL-001", "dana")].ShouldBeTrue();
    }

    [Fact]
    public void Case_B_intersection_through_arrow_truth_is_pinned()
    {
        SpikeData.CaseB_Expected[("EL-001", "dr-smith")].ShouldBeTrue();
        SpikeData.CaseB_Expected[("EL-001", "jones")].ShouldBeFalse();
        SpikeData.CaseB_Expected[("EL-001", "outsider")].ShouldBeFalse();
    }
}
