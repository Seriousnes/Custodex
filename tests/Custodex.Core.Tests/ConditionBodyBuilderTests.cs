using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests;

public class ConditionBodyBuilderTests
{
    [Fact]
    public void Condition_overload_attaches_a_real_body()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("within_hours",
                p => p.Int("start").Int("end"),
                b => b.And(
                    b.Ge(b.Hour(b.Now()), b.Param("start")),
                    b.Lt(b.Hour(b.Now()), b.Param("end"))))
            .Build();

        var cond = schema.Conditions.Single();
        cond.Name.ShouldBe("within_hours");
        cond.Parameters.Count.ShouldBe(2);
        cond.Body.ShouldBeOfType<BoolOp>().Op.ShouldBe(BoolConnective.And);
    }

    [Fact]
    public void Params_only_overload_still_attaches_empty_body()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("legacy", c => c.Int("n"))
            .Build();

        schema.Conditions.Single().Body.ShouldBeOfType<EmptyConditionBody>();
    }

    [Fact]
    public void Evaluator_runs_a_builder_produced_body()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("at_least",
                p => p.Int("n"),
                b => b.Ge(b.Attribute("weight"), b.Param("n")))
            .Build();

        var def = schema.Conditions.Single();
        var result = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = 50 },
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "dr-smith"),
                new Dictionary<string, object?>()),
            new Dictionary<string, object?> { ["n"] = 30 });

        result.Allowed.ShouldBeTrue();
    }
}
