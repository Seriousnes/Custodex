using Custodex.Abstractions;
using Shouldly;

namespace Custodex.Core.Tests;

public class SchemaBuilderTests
{
    [Fact]
    public void Builds_animal_schema_with_relations_permission_and_condition()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("animal", t => t
                .Relation("medicator", s => s.User().SubjectSet("group", "member"))
                .Relation("enclosure", s => s.Type("enclosure"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("medicator").Arrow("enclosure", "edit").Exclude(x => x.Relation("blocked"))))
            .Condition("within_hours", c => c.Int("start").Int("end"))
            .Build();

        schema.Version.ShouldBe("v1");
        var animal = schema.Types.Single(x => x.Name == "animal");
        animal.Relations.Select(r => r.Name).ShouldBe(new[] { "medicator", "enclosure", "blocked" });
        animal.Permissions.Single().Name.ShouldBe("edit");
        animal.Permissions.Single().Expression.ShouldBeOfType<Exclude>();
        schema.Conditions.Single().Name.ShouldBe("within_hours");
        schema.Conditions.Single().Parameters.Count.ShouldBe(2);
    }
}
