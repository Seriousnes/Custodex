using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests;

public class ConditionBodyBuilderTests
{
    [Fact]
    public void Condition_overload_attaches_a_real_body()
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

        var cond = schema.Conditions.Single();
        cond.Name.ShouldBe(condition);
        cond.Parameters.Count.ShouldBe(2);
        cond.Body.ShouldBeOfType<BoolOp>().Op.ShouldBe(BoolConnective.And);
    }

    [Fact]
    public void Params_only_overload_still_attaches_empty_body()
    {
        var world = TestWorld.New();
        var schema = new SchemaBuilder(world.Version)
            .Condition(world.ConditionName(), c => c.Int(world.ParamName()))
            .Build();

        schema.Conditions.Single().Body.ShouldBeOfType<EmptyConditionBody>();
    }

    [Fact]
    public void Evaluator_runs_a_builder_produced_body()
    {
        var world = TestWorld.New();
        var condition = world.ConditionName();
        var n = world.ParamName();
        var measure = world.ParamName();
        var schema = new SchemaBuilder(world.Version)
            .Condition(condition,
                p => p.Int(n),
                b => b.Ge(b.Attribute(measure), b.Param(n)))
            .Build();

        var def = schema.Conditions.Single();
        var result = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [measure] = 50 },
            new RequestContext(DateTimeOffset.UnixEpoch, world.User(world.SubjectId()),
                new Dictionary<string, object?>()),
            new Dictionary<string, object?> { [n] = 30 });

        result.Allowed.ShouldBeTrue();
    }
}
