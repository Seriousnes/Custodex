using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Validation;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Validation;

public class RecursionTerminationTests
{
    [Fact]
    public void Self_referential_permission_is_a_cycle()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("animal",
                [],
                [new PermissionDef("edit", new RelationRef("edit"))])],   // edit -> edit
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle") && e.Contains("animal.edit"));
    }

    [Fact]
    public void Permission_cycle_through_an_arrow_is_detected()
    {
        // animal.edit -> enclosure.edit -> animal.edit  (via back-arrows)
        var schema = new Schema("v1",
            [
                new EntityTypeDef("animal",
                    [new RelationDef("enclosure", [new SubjectTypeRef("enclosure")])],
                    [new PermissionDef("edit", new Arrow("enclosure", "edit"))]),
                new EntityTypeDef("enclosure",
                    [new RelationDef("home_animal", [new SubjectTypeRef("animal")])],
                    [new PermissionDef("edit", new Arrow("home_animal", "edit"))]),
            ],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle"));
    }

    [Fact]
    public void Nested_permissions_without_a_cycle_terminate()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("medicator", s => s.User())
                .Permission("edit", p => p.Relation("medicator"))
                .Permission("manage", p => p.Relation("edit")))   // manage -> edit -> medicator (relation, stops)
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Subject_set_self_reference_is_not_a_permission_cycle()
    {
        // group.member fills with group#member (nesting) — a relation filler, not a permission edge.
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t
                .Relation("member", s => s.User().SubjectSet("group", "member"))
                .Permission("read", p => p.Relation("member")))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
