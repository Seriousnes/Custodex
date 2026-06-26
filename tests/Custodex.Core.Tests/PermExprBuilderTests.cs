using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests;

public class PermExprBuilderTests
{
    [Fact]
    public void Chained_terms_union_then_exclude_wraps_accumulated()
    {
        var world = TestWorld.New();
        var grant = world.Relation();
        var link = world.Relation();
        var target = world.Permission();
        var blocked = world.Relation();
        var expr = new PermExprBuilder()
            .Relation(grant)
            .Arrow(link, target)
            .Exclude(x => x.Relation(blocked))
            .Build();

        var exclude = expr.ShouldBeOfType<Exclude>();
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe(blocked);
        var union = exclude.Left.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe(grant);
        var arrow = union.Right.ShouldBeOfType<Arrow>();
        arrow.Relation.ShouldBe(link);
        arrow.Permission.ShouldBe(target);
    }

    [Fact]
    public void Intersect_and_conditioned_wrap_in_order()
    {
        var world = TestWorld.New();
        var left = world.Relation();
        var right = world.Relation();
        var condition = world.ConditionName();
        var expr = new PermExprBuilder()
            .Relation(left)
            .Intersect(x => x.Relation(right))
            .Conditioned(condition)
            .Build();

        var cond = expr.ShouldBeOfType<Conditioned>();
        cond.ConditionName.ShouldBe(condition);
        var inter = cond.Inner.ShouldBeOfType<Intersect>();
        inter.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe(left);
        inter.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe(right);
    }

    [Fact]
    public void Build_with_no_terms_throws()
    {
        Should.Throw<InvalidOperationException>(() => new PermExprBuilder().Build());
    }
}
