using Custodex.Abstractions;
using Custodex.Core.Dsl.Parsing;

using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class PermissionParseTests
{
    private static PermissionDef ParsePerm(string expr)
    {
        var src = $"type widget {{ permission view = {expr} }}";
        return SchemaParser.Parse(src).Types[0].Permissions[0];
    }

    [Fact]
    public void Bare_relation_name_parses_as_relation_ref()
    {
        var perm = ParsePerm("owner");

        perm.Expression.ShouldBeOfType<RelationRef>()
            .Relation.ShouldBe("owner");
    }

    [Fact]
    public void Arrow_expression_parses_correctly()
    {
        var perm = ParsePerm("container->view");

        var arrow = perm.Expression.ShouldBeOfType<Arrow>();
        arrow.Relation.ShouldBe("container");
        arrow.Permission.ShouldBe("view");
    }

    [Fact]
    public void Union_of_two_relation_refs()
    {
        var perm = ParsePerm("owner + editor");

        var union = perm.Expression.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("owner");
        union.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("editor");
    }

    [Fact]
    public void Intersect_of_two_relation_refs()
    {
        var perm = ParsePerm("owner & editor");

        var intersect = perm.Expression.ShouldBeOfType<Intersect>();
        intersect.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("owner");
        intersect.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("editor");
    }

    [Fact]
    public void Three_ops_are_left_associative_producing_correct_tree()
    {
        var perm = ParsePerm("editor + container->update - blocked");

        var exclude = perm.Expression.ShouldBeOfType<Exclude>();
        var union = exclude.Left.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("editor");
        var arrow = union.Right.ShouldBeOfType<Arrow>();
        arrow.Relation.ShouldBe("container");
        arrow.Permission.ShouldBe("update");
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("blocked");
    }

    [Fact]
    public void Parentheses_override_left_association()
    {
        var perm = ParsePerm("owner + (editor - blocked)");

        var union = perm.Expression.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("owner");
        var exclude = union.Right.ShouldBeOfType<Exclude>();
        exclude.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("editor");
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("blocked");
    }

    [Fact]
    public void With_wraps_its_left_operand_in_conditioned()
    {
        var perm = ParsePerm("owner with within_window");

        var conditioned = perm.Expression.ShouldBeOfType<Conditioned>();
        conditioned.Inner.ShouldBeOfType<RelationRef>().Relation.ShouldBe("owner");
        conditioned.ConditionName.ShouldBe("within_window");
    }

    [Fact]
    public void With_binds_tightest_to_immediate_left_primary()
    {
        var perm = ParsePerm("owner + editor with within_window");

        var union = perm.Expression.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("owner");
        var conditioned = union.Right.ShouldBeOfType<Conditioned>();
        conditioned.Inner.ShouldBeOfType<RelationRef>().Relation.ShouldBe("editor");
        conditioned.ConditionName.ShouldBe("within_window");
    }
}
