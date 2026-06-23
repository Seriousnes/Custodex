using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Conditions;

public class NullConditionEvaluatorTests
{
    [Fact]
    public void Null_evaluator_treats_every_condition_as_satisfied()
    {
        var eval = new NullConditionEvaluator();
        var def = new ConditionDef("within_hours",
            new[] { new ConditionParam("start", ConditionType.Int) },
            new TrueBody());
        var inv = new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8 });
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"),
            new Dictionary<string, object?>());

        eval.Evaluate(def, inv, new Dictionary<string, object?>(), ctx).ShouldBeTrue();
    }

    private sealed record TrueBody : ConditionExpr;
}
