using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class NullConditionEvaluatorTests
{
    [Fact]
    public void Null_evaluator_treats_every_condition_as_satisfied()
    {
        var world = TestWorld.New();
        var condition = world.ConditionName();
        var param = world.ParamName();
        var eval = new NullConditionEvaluator();
        var def = new ConditionDef(condition,
            new[] { new ConditionParam(param, ConditionType.Int) },
            new TrueBody());
        var inv = new ConditionRef(condition, new Dictionary<string, object?> { [param] = 8 });
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, world.User(world.SubjectId()),
            new Dictionary<string, object?>());

        eval.Evaluate(def, inv, new Dictionary<string, object?>(), ctx).ShouldBeTrue();
    }

    private sealed record TrueBody : ConditionExpr;
}
