using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Core.Tests.Abstractions;

public class CheckResultTests
{
    [Fact]
    public void Bool_constructor_maps_to_allow_and_deny()
    {
        new CheckResult(true).Decision.ShouldBe(CheckDecision.Allow);
        new CheckResult(true).Allowed.ShouldBeTrue();
        new CheckResult(false).Decision.ShouldBe(CheckDecision.Deny);
        new CheckResult(false).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Allow_and_deny_carry_no_unmet_conditions()
    {
        new CheckResult(CheckDecision.Allow).UnmetConditions.ShouldBeEmpty();
        new CheckResult(CheckDecision.Deny).UnmetConditions.ShouldBeEmpty();
    }

    [Fact]
    public void Conditional_is_not_allowed_and_surfaces_the_unmet_caveat()
    {
        var unmet = new[] { new UnmetCondition("window", ["opensAt", "closesAt"]) };

        var result = new CheckResult(CheckDecision.Conditional, unmet);

        result.Decision.ShouldBe(CheckDecision.Conditional);
        result.Allowed.ShouldBeFalse();
        result.UnmetConditions.ShouldBe(unmet);
        result.UnmetConditions[0].MissingKeys.ShouldBe(["opensAt", "closesAt"]);
    }
}
