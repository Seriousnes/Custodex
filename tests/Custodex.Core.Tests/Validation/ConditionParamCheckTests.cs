using Custodex.Abstractions;
using Custodex.Core.Validation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Validation;

public class ConditionParamCheckTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _start;
    private readonly string _end;
    private readonly ConditionDef _twoIntParams;

    public ConditionParamCheckTests()
    {
        _start = _world.ParamName();
        _end = _world.ParamName();
        _twoIntParams = new ConditionDef(
            _world.ConditionName(),
            [new ConditionParam(_start, ConditionType.Int), new ConditionParam(_end, ConditionType.Int)],
            new EmptyConditionBody());
    }

    [Fact]
    public void Matching_int_parameters_have_no_errors()
    {
        var errors = ConditionParamChecker.Check(_twoIntParams,
            new Dictionary<string, object?> { [_start] = 8, [_end] = 18 });

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Wrong_typed_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(_twoIntParams,
            new Dictionary<string, object?> { [_start] = "8", [_end] = 18 });

        errors.ShouldContain(e => e.Contains(_start) && e.Contains("Int"));
    }

    [Fact]
    public void Missing_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(_twoIntParams,
            new Dictionary<string, object?> { [_start] = 8 });

        errors.ShouldContain(e => e.Contains(_end) && e.Contains("missing"));
    }

    [Fact]
    public void Unknown_parameter_is_reported()
    {
        var extra = _world.ParamName();
        var errors = ConditionParamChecker.Check(_twoIntParams,
            new Dictionary<string, object?> { [_start] = 8, [_end] = 18, [extra] = 1 });

        errors.ShouldContain(e => e.Contains(extra) && e.Contains("not declared"));
    }

    [Fact]
    public void Double_param_accepts_integer_value()
    {
        var n = _world.ParamName();
        var def = new ConditionDef(_world.ConditionName(),
            [new ConditionParam(n, ConditionType.Double)], new EmptyConditionBody());

        ConditionParamChecker.Check(def, new Dictionary<string, object?> { [n] = 3 }).ShouldBeEmpty();
    }
}
