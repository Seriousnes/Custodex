using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Validation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Validation;

public class ConditionBodyCheckTests
{
    [Fact]
    public void Well_typed_bounded_window_body_passes()
    {
        var world = TestWorld.New();
        var condition = world.ConditionName();
        var start = world.ParamName();
        var end = world.ParamName();
        var schema = new SchemaBuilder(world.Version)
            .Condition(condition,
                p => p.Int(start).Int(end),
                b => b.And(
                    b.Ge(b.Hour(b.Now()), b.Param(start)),
                    b.Lt(b.Hour(b.Now()), b.Param(end))))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Body_referencing_an_undeclared_parameter_fails()
    {
        var world = TestWorld.New();
        var declared = world.ParamName();
        var undeclared = world.ParamName();
        var def = new ConditionDef(world.ConditionName(), [new ConditionParam(declared, ConditionType.Int)],
            new Compare(new ParamRef(undeclared), CompareOp.Ge, new LiteralInt(1)));
        var schema = new Schema(world.Version, [], [def]);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(undeclared));
    }

    [Fact]
    public void Body_comparing_a_string_param_to_a_number_fails()
    {
        var world = TestWorld.New();
        var s = world.ParamName();
        var def = new ConditionDef(world.ConditionName(), [new ConditionParam(s, ConditionType.String)],
            new Compare(new ParamRef(s), CompareOp.Lt, new LiteralInt(1)));
        var schema = new Schema(world.Version, [], [def]);

        SchemaValidator.Validate(schema).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Non_boolean_top_level_body_fails()
    {
        var world = TestWorld.New();
        var n = world.ParamName();
        var def = new ConditionDef(world.ConditionName(), [new ConditionParam(n, ConditionType.Int)],
            new ParamRef(n));
        var schema = new Schema(world.Version, [], [def]);

        SchemaValidator.Validate(schema).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Empty_body_from_params_only_overload_is_skipped()
    {
        var world = TestWorld.New();
        var schema = new SchemaBuilder(world.Version)
            .Condition(world.ConditionName(), c => c.Int(world.ParamName())).Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
