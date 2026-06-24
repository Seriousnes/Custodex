using Custodex.Core.Conditions;
using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class CelValueTests
{
    [Fact]
    public void Typed_factories_round_trip()
    {
        CelValue.Int(8).AsLong().ShouldBe(8);
        CelValue.Double(1.5).AsDouble().ShouldBe(1.5);
        CelValue.String("x").AsString().ShouldBe("x");
        CelValue.Bool(true).AsBool().ShouldBeTrue();
    }

    [Fact]
    public void Int_widens_to_double()
    {
        CelValue.Int(3).AsDouble().ShouldBe(3.0);
    }

    [Fact]
    public void Error_result_is_a_deny_with_a_diagnostic()
    {
        var r = ConditionResult.Error("missing attribute 'weight'");
        r.Allowed.ShouldBeFalse();
        r.Diagnostic!.ShouldContain("weight");
    }
}
