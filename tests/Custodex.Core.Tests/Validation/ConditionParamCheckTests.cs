using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Validation;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Validation;

public class ConditionParamCheckTests
{
    private static readonly ConditionDef WithinHours = new(
        "within_hours",
        [new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int)],
        new EmptyConditionBody());

    [Fact]
    public void Matching_int_parameters_have_no_errors()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 });

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Wrong_typed_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = "8", ["end"] = 18 });

        errors.ShouldContain(e => e.Contains("start") && e.Contains("Int"));
    }

    [Fact]
    public void Missing_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = 8 });

        errors.ShouldContain(e => e.Contains("end") && e.Contains("missing"));
    }

    [Fact]
    public void Unknown_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18, ["extra"] = 1 });

        errors.ShouldContain(e => e.Contains("extra") && e.Contains("not declared"));
    }

    [Fact]
    public void Double_param_accepts_integer_value()
    {
        var def = new ConditionDef("at_least",
            [new ConditionParam("n", ConditionType.Double)], new EmptyConditionBody());

        ConditionParamChecker.Check(def, new Dictionary<string, object?> { ["n"] = 3 }).ShouldBeEmpty();
    }
}
