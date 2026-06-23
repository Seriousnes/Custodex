using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Conditions;

public class ConditionExprNodesTests
{
    [Fact]
    public void Nodes_derive_from_condition_expr_and_are_pattern_matchable()
    {
        ConditionExpr expr = new Compare(
            new AttributeRef("weight"), CompareOp.Ge, new ParamRef("n"));

        var label = expr switch
        {
            Compare c when c.Op == CompareOp.Ge => "ge",
            _ => "other",
        };
        label.ShouldBe("ge");
        expr.ShouldBeAssignableTo<ConditionExpr>();
    }

    [Fact]
    public void Within_hours_body_is_expressible_as_a_tree()
    {
        // context.now.hour >= start && context.now.hour < end
        ConditionExpr body = new BoolOp(
            new Compare(new HourOf(new ContextNow()), CompareOp.Ge, new ParamRef("start")),
            BoolConnective.And,
            new Compare(new HourOf(new ContextNow()), CompareOp.Lt, new ParamRef("end")));

        body.ShouldBeOfType<BoolOp>().Op.ShouldBe(BoolConnective.And);
    }
}
