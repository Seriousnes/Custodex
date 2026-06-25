using Custodex.Abstractions;
using Custodex.Core.Dsl.Parsing;
using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class RelationParseTests
{
    [Fact]
    public void Empty_source_produces_schema_with_no_types()
    {
        var schema = SchemaParser.Parse("");

        schema.Types.ShouldBeEmpty();
        schema.Conditions.ShouldBeEmpty();
    }

    [Fact]
    public void Empty_type_body_uses_default_version()
    {
        var schema = SchemaParser.Parse("type widget { }");

        schema.Version.ShouldBe("v1");
        schema.Types.ShouldHaveSingleItem();
        schema.Types[0].Name.ShouldBe("widget");
    }

    [Fact]
    public void Version_overload_uses_supplied_version()
    {
        var schema = SchemaParser.Parse("type widget { }", "v2");

        schema.Version.ShouldBe("v2");
    }

    [Fact]
    public void Simple_type_reference_parses_as_any_instance()
    {
        var schema = SchemaParser.Parse("type widget { relation owner: user }");

        var rel = schema.Types[0].Relations.Single();
        rel.Name.ShouldBe("owner");
        rel.AllowedSubjects.ShouldHaveSingleItem();
        rel.AllowedSubjects[0].ShouldBe(new SubjectTypeRef("user"));
    }

    [Fact]
    public void Subject_set_parses_correctly()
    {
        var schema = SchemaParser.Parse("type widget { relation member: group#member }");

        var rel = schema.Types[0].Relations.Single();
        rel.AllowedSubjects[0].ShouldBe(new SubjectTypeRef("group", "member", false));
    }

    [Fact]
    public void Wildcard_subject_parses_correctly()
    {
        var schema = SchemaParser.Parse("type widget { relation viewer: user:* }");

        var rel = schema.Types[0].Relations.Single();
        rel.AllowedSubjects[0].ShouldBe(new SubjectTypeRef("user", null, true));
    }

    [Fact]
    public void Multiple_subjects_in_pipe_list_all_parse()
    {
        const string src = "type container { relation member: user | group#member | user:* }";
        var schema = SchemaParser.Parse(src);

        var subjects = schema.Types[0].Relations[0].AllowedSubjects;
        subjects.Count.ShouldBe(3);
        subjects[0].ShouldBe(new SubjectTypeRef("user"));
        subjects[1].ShouldBe(new SubjectTypeRef("group", "member", false));
        subjects[2].ShouldBe(new SubjectTypeRef("user", null, true));
    }

    [Fact]
    public void Multiple_types_parse_in_order()
    {
        const string src = """
            type group { relation member: user }
            type widget { relation owner: user }
            """;
        var schema = SchemaParser.Parse(src);

        schema.Types.Count.ShouldBe(2);
        schema.Types[0].Name.ShouldBe("group");
        schema.Types[1].Name.ShouldBe("widget");
    }
}
