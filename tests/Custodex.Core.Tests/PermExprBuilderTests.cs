using Custodex.Abstractions;
using Shouldly;

namespace Custodex.Core.Tests;

public class PermExprBuilderTests
{
    [Fact]
    public void Chained_terms_union_then_exclude_wraps_accumulated()
    {
        var expr = new PermExprBuilder()
            .Relation("medicator")
            .Arrow("enclosure", "edit")
            .Exclude(x => x.Relation("blocked"))
            .Build();

        // Expect: Exclude(Union(RelationRef medicator, Arrow enclosure->edit), RelationRef blocked)
        var exclude = expr.ShouldBeOfType<Exclude>();
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("blocked");
        var union = exclude.Left.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("medicator");
        var arrow = union.Right.ShouldBeOfType<Arrow>();
        arrow.Relation.ShouldBe("enclosure");
        arrow.Permission.ShouldBe("edit");
    }

    [Fact]
    public void Intersect_and_conditioned_wrap_in_order()
    {
        var expr = new PermExprBuilder()
            .Relation("a")
            .Intersect(x => x.Relation("b"))
            .Conditioned("within_hours")
            .Build();

        var cond = expr.ShouldBeOfType<Conditioned>();
        cond.ConditionName.ShouldBe("within_hours");
        var inter = cond.Inner.ShouldBeOfType<Intersect>();
        inter.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("a");
        inter.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("b");
    }

    [Fact]
    public void Build_with_no_terms_throws()
    {
        Should.Throw<InvalidOperationException>(() => new PermExprBuilder().Build());
    }
}
