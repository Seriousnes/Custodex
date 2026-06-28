using Custodex.Abstractions;
using Custodex.Studio.Components.Schema;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class PermExprFormatterTests
{
    [Fact]
    public void RelationRef_formats_as_the_relation_name()
    {
        var world = TestWorld.New();
        var rel = world.Relation();

        PermExprFormatter.Format(new RelationRef(rel)).ShouldBe(rel);
    }

    [Fact]
    public void Union_joins_with_plus()
    {
        var world = TestWorld.New();
        var a = world.Relation();
        var b = world.Relation();

        PermExprFormatter.Format(new Union(new RelationRef(a), new RelationRef(b)))
            .ShouldBe($"{a} + {b}");
    }

    [Fact]
    public void Intersect_joins_with_ampersand()
    {
        var world = TestWorld.New();
        var a = world.Relation();
        var b = world.Relation();

        PermExprFormatter.Format(new Intersect(new RelationRef(a), new RelationRef(b)))
            .ShouldBe($"{a} & {b}");
    }

    [Fact]
    public void Exclude_joins_with_minus()
    {
        var world = TestWorld.New();
        var a = world.Relation();
        var b = world.Relation();

        PermExprFormatter.Format(new Exclude(new RelationRef(a), new RelationRef(b)))
            .ShouldBe($"{a} - {b}");
    }

    [Fact]
    public void Arrow_formats_as_relation_arrow_permission()
    {
        var world = TestWorld.New();
        var rel = world.Relation();
        var perm = world.Permission();

        PermExprFormatter.Format(new Arrow(rel, perm)).ShouldBe($"{rel}->{perm}");
    }

    [Fact]
    public void Conditioned_appends_with_condition()
    {
        var world = TestWorld.New();
        var rel = world.Relation();
        var cond = world.ConditionName();

        PermExprFormatter.Format(new Conditioned(new RelationRef(rel), cond))
            .ShouldBe($"{rel} with {cond}");
    }

    [Fact]
    public void Nested_intersection_inside_union_is_parenthesized()
    {
        var world = TestWorld.New();
        var a = world.Relation();
        var b = world.Relation();
        var c = world.Relation();

        PermExprFormatter.Format(new Union(new RelationRef(a), new Intersect(new RelationRef(b), new RelationRef(c))))
            .ShouldBe($"{a} + ({b} & {c})");
    }

    [Fact]
    public void Nested_union_inside_intersection_is_parenthesized_on_the_left()
    {
        var world = TestWorld.New();
        var a = world.Relation();
        var b = world.Relation();
        var c = world.Relation();

        PermExprFormatter.Format(new Intersect(new Union(new RelationRef(a), new RelationRef(b)), new RelationRef(c)))
            .ShouldBe($"({a} + {b}) & {c}");
    }

    [Fact]
    public void Same_kind_nesting_is_still_parenthesized()
    {
        var world = TestWorld.New();
        var a = world.Relation();
        var b = world.Relation();
        var c = world.Relation();

        PermExprFormatter.Format(new Union(new Union(new RelationRef(a), new RelationRef(b)), new RelationRef(c)))
            .ShouldBe($"({a} + {b}) + {c}");
    }
}
