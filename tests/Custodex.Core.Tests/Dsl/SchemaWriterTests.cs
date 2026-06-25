using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Dsl;
using Custodex.Core.Dsl.Parsing;
using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class SchemaWriterTests
{
    [Fact]
    public void Wildcard_subject_renders_as_type_colon_star()
    {
        var schema = new Schema("v1",
        [
            new EntityTypeDef("widget", [new RelationDef("viewer", [new SubjectTypeRef("user", null, true)])], [])
        ], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("user:*");
    }

    [Fact]
    public void Subject_set_renders_as_type_hash_relation()
    {
        var schema = new Schema("v1",
        [
            new EntityTypeDef("widget", [new RelationDef("member", [new SubjectTypeRef("group", "member", false)])], [])
        ], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("group#member");
    }

    [Fact]
    public void Relation_ref_renders_as_bare_name()
    {
        var perm = new PermissionDef("view", new RelationRef("owner"));
        var schema = new Schema("v1", [new EntityTypeDef("widget", [], [perm])], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("permission view = owner");
    }

    [Fact]
    public void Arrow_renders_as_rel_arrow_perm()
    {
        var perm = new PermissionDef("view", new Arrow("container", "view"));
        var schema = new Schema("v1", [new EntityTypeDef("widget", [], [perm])], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("container->view");
    }

    [Fact]
    public void Union_of_right_binary_child_adds_parens()
    {
        var expr = new Union(new RelationRef("owner"), new Exclude(new RelationRef("editor"), new RelationRef("blocked")));
        var perm = new PermissionDef("view", expr);
        var schema = new Schema("v1", [new EntityTypeDef("widget", [], [perm])], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("owner + (editor - blocked)");
    }

    [Fact]
    public void Left_binary_child_does_not_add_parens()
    {
        var expr = new Exclude(new Union(new RelationRef("editor"), new Arrow("container", "update")), new RelationRef("blocked"));
        var perm = new PermissionDef("view", expr);
        var schema = new Schema("v1", [new EntityTypeDef("widget", [], [perm])], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("editor + container->update - blocked");
        text.ShouldNotContain("(editor");
    }

    [Fact]
    public void Conditioned_on_binary_inner_wraps_in_parens()
    {
        var expr = new Conditioned(new Union(new RelationRef("owner"), new RelationRef("editor")), "within_window");
        var perm = new PermissionDef("view", expr);
        var schema = new Schema("v1", [new EntityTypeDef("widget", [], [perm])], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("(owner + editor) with within_window");
    }

    [Fact]
    public void Conditioned_on_relation_ref_does_not_add_parens()
    {
        var expr = new Conditioned(new RelationRef("owner"), "within_window");
        var perm = new PermissionDef("view", expr);
        var schema = new Schema("v1", [new EntityTypeDef("widget", [], [perm])], []);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("owner with within_window");
        text.ShouldNotContain("(owner)");
    }

    [Fact]
    public void Param_only_condition_renders_head_only()
    {
        var cond = new ConditionDef("within_window", [new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int)], new EmptyConditionBody());
        var schema = new Schema("v1", [], [cond]);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("condition within_window(start: int, end: int)");
        text.ShouldNotContain("=");
    }

    [Fact]
    public void Condition_body_renders_with_equals()
    {
        var body = new Compare(new AttributeRef("weight"), CompareOp.Ge, new ParamRef("n"));
        var cond = new ConditionDef("at_least", [new ConditionParam("n", ConditionType.Int)], body);
        var schema = new Schema("v1", [], [cond]);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("condition at_least(n: int) =");
        text.ShouldContain("resource[\"weight\"]");
    }

    [Fact]
    public void Round_trip_parse_write_parse_is_stable()
    {
        const string src = """
            type group {
                relation member: user | group#member
            }
            type widget {
                relation owner: user
                relation viewer: user | group#member | user:*
                permission view = owner + viewer
            }
            condition within_window(start: int, end: int) = (hour(context.now) >= start) && (hour(context.now) < end)
            """;

        var schema1 = SchemaParser.Parse(src);
        var text1 = SchemaWriter.Write(schema1);
        var schema2 = SchemaParser.Parse(text1);
        var text2 = SchemaWriter.Write(schema2);

        text1.ShouldBe(text2);
    }
}
