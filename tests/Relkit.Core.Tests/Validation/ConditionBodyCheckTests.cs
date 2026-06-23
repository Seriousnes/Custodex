using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Validation;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Validation;

public class ConditionBodyCheckTests
{
    [Fact]
    public void Well_typed_within_hours_body_passes()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("within_hours",
                p => p.Int("start").Int("end"),
                b => b.And(
                    b.Ge(b.Hour(b.Now()), b.Param("start")),
                    b.Lt(b.Hour(b.Now()), b.Param("end"))))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Body_referencing_an_undeclared_parameter_fails()
    {
        var def = new ConditionDef("bad", [new ConditionParam("n", ConditionType.Int)],
            new Compare(new ParamRef("ghost"), CompareOp.Ge, new LiteralInt(1)));
        var schema = new Schema("v1", [], [def]);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("ghost"));
    }

    [Fact]
    public void Body_comparing_a_string_param_to_a_number_fails()
    {
        var def = new ConditionDef("bad", [new ConditionParam("s", ConditionType.String)],
            new Compare(new ParamRef("s"), CompareOp.Lt, new LiteralInt(1)));
        var schema = new Schema("v1", [], [def]);

        SchemaValidator.Validate(schema).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Non_boolean_top_level_body_fails()
    {
        var def = new ConditionDef("bad", [new ConditionParam("n", ConditionType.Int)],
            new ParamRef("n"));   // top-level Int, not Bool
        var schema = new Schema("v1", [], [def]);

        SchemaValidator.Validate(schema).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Empty_body_from_params_only_overload_is_skipped()
    {
        var schema = new SchemaBuilder("v1").Condition("legacy", c => c.Int("n")).Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
