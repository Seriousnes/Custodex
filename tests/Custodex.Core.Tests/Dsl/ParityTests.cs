using Custodex.Abstractions;
using Custodex.Core.Dsl;
using Custodex.Core.Dsl.Parsing;
using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class ParityTests
{
    private const string NeutralSchemaText = """
        type group {
            relation member: user | group#member
        }
        type widget {
            relation editor: user | group#member
            relation container: container
            relation blocked: user | group#member
            permission edit = editor + container->edit - blocked
        }
        type container {
            relation editor: user | group#member
            relation blocked: user | group#member
            permission edit = editor - blocked
        }
        condition within_window(start: int, end: int)
        """;

    private static Schema BuilderSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member",
            s => s.Type("user").SubjectSet("group", "member")))
        .Type("widget", t => t
            .Relation("editor", s => s.Type("user").SubjectSet("group", "member"))
            .Relation("container", s => s.Type("container"))
            .Relation("blocked", s => s.Type("user").SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor").Arrow("container", "edit").Exclude(x => x.Relation("blocked"))))
        .Type("container", t => t
            .Relation("editor", s => s.Type("user").SubjectSet("group", "member"))
            .Relation("blocked", s => s.Type("user").SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Condition("within_window", c => c.Int("start").Int("end"))
        .Build();

    [Fact]
    public void Parsed_schema_version_matches_builder()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        parsed.Version.ShouldBe(built.Version);
    }

    [Fact]
    public void Parsed_schema_has_same_type_names_as_builder()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        parsed.Types.Select(t => t.Name).ShouldBe(built.Types.Select(t => t.Name));
    }

    [Fact]
    public void Parsed_schema_group_type_relations_match_builder()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        var parsedGroup = parsed.Types.Single(t => t.Name == "group");
        var builtGroup = built.Types.Single(t => t.Name == "group");

        parsedGroup.Relations.Count.ShouldBe(builtGroup.Relations.Count);
        var pRel = parsedGroup.Relations[0];
        var bRel = builtGroup.Relations[0];
        pRel.Name.ShouldBe(bRel.Name);
        pRel.AllowedSubjects.Count.ShouldBe(bRel.AllowedSubjects.Count);
        for (var i = 0; i < pRel.AllowedSubjects.Count; i++)
            pRel.AllowedSubjects[i].ShouldBe(bRel.AllowedSubjects[i]);
    }

    [Fact]
    public void Parsed_schema_widget_relations_match_builder()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        var parsedWidget = parsed.Types.Single(t => t.Name == "widget");
        var builtWidget = built.Types.Single(t => t.Name == "widget");

        parsedWidget.Relations.Count.ShouldBe(builtWidget.Relations.Count);
        for (var i = 0; i < parsedWidget.Relations.Count; i++)
        {
            var pr = parsedWidget.Relations[i];
            var br = builtWidget.Relations[i];
            pr.Name.ShouldBe(br.Name);
            pr.AllowedSubjects.Count.ShouldBe(br.AllowedSubjects.Count);
            for (var j = 0; j < pr.AllowedSubjects.Count; j++)
                pr.AllowedSubjects[j].ShouldBe(br.AllowedSubjects[j]);
        }
    }

    [Fact]
    public void Parsed_schema_container_type_matches_builder()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        var parsedContainer = parsed.Types.Single(t => t.Name == "container");
        var builtContainer = built.Types.Single(t => t.Name == "container");

        parsedContainer.Relations.Count.ShouldBe(builtContainer.Relations.Count);
        for (var i = 0; i < parsedContainer.Relations.Count; i++)
        {
            var pr = parsedContainer.Relations[i];
            var br = builtContainer.Relations[i];
            pr.Name.ShouldBe(br.Name);
            pr.AllowedSubjects.Count.ShouldBe(br.AllowedSubjects.Count);
            for (var j = 0; j < pr.AllowedSubjects.Count; j++)
                pr.AllowedSubjects[j].ShouldBe(br.AllowedSubjects[j]);
        }

        parsedContainer.Permissions.Count.ShouldBe(builtContainer.Permissions.Count);
        for (var i = 0; i < parsedContainer.Permissions.Count; i++)
        {
            parsedContainer.Permissions[i].Name.ShouldBe(builtContainer.Permissions[i].Name);
            parsedContainer.Permissions[i].Expression.ShouldBe(builtContainer.Permissions[i].Expression);
        }
    }

    [Fact]
    public void Parsed_schema_widget_permission_expression_matches_builder()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        var parsedWidget = parsed.Types.Single(t => t.Name == "widget");
        var builtWidget = built.Types.Single(t => t.Name == "widget");

        var parsedExpr = parsedWidget.Permissions.Single(p => p.Name == "edit").Expression;
        var builtExpr = builtWidget.Permissions.Single(p => p.Name == "edit").Expression;

        parsedExpr.ShouldBe(builtExpr);
    }

    [Fact]
    public void Parsed_schema_condition_matches_builder()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        parsed.Conditions.Count.ShouldBe(built.Conditions.Count);
        var parsedCond = parsed.Conditions[0];
        var builtCond = built.Conditions[0];

        parsedCond.Name.ShouldBe(builtCond.Name);
        parsedCond.Body.ShouldBe(builtCond.Body);
        parsedCond.Parameters.Count.ShouldBe(builtCond.Parameters.Count);
        for (var i = 0; i < parsedCond.Parameters.Count; i++)
            parsedCond.Parameters[i].ShouldBe(builtCond.Parameters[i]);
    }

    [Fact]
    public void Write_of_parsed_and_write_of_built_produce_same_text()
    {
        var parsed = SchemaParser.Parse(NeutralSchemaText);
        var built = BuilderSchema();

        SchemaWriter.Write(parsed).ShouldBe(SchemaWriter.Write(built));
    }
}
