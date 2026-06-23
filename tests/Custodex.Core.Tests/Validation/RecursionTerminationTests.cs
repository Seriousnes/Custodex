using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Validation;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Validation;

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

    [Fact]
    public void Permission_referencing_a_same_named_relation_is_not_a_cycle()
    {
        // 'enclosure' has a relation 'is_quarantine' AND a permission 'is_quarantine' = RelationRef("is_quarantine").
        // The authorizer resolves the RelationRef as the RELATION (it never recurses into a same-named
        // permission), so this is NOT a permission self-cycle and must validate.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("is_quarantine", s => s.Wildcard("user"))
                .Permission("is_quarantine", p => p.Relation("is_quarantine")))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
