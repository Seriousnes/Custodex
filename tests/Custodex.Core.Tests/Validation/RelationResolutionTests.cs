using Custodex.Abstractions;
using Custodex.Core.Validation;
using Shouldly;

namespace Custodex.Core.Tests.Validation;

public class RelationResolutionTests
{
    [Fact]
    public void Valid_schema_with_relation_and_nested_permission_passes()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("medicator", s => s.User())
                .Permission("edit", p => p.Relation("medicator"))
                .Permission("manage", p => p.Relation("edit")))   // RelationRef naming a permission (nesting)
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void RelationRef_to_unknown_name_fails_with_message()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("medicator", s => s.User())
                .Permission("edit", p => p.Relation("ghost")))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("animal") && e.Contains("ghost"));
    }

    [Fact]
    public void Duplicate_relation_name_on_a_type_fails()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("animal",
                [new RelationDef("medicator", [new SubjectTypeRef("user")]),
                 new RelationDef("medicator", [new SubjectTypeRef("user")])],
                [])],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("medicator") && e.Contains("duplicate"));
    }
}
