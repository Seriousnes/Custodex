using Custodex.Abstractions;
using Custodex.Core.Validation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Validation;

public class RelationResolutionTests
{
    [Fact]
    public void Valid_schema_with_relation_and_nested_permission_passes()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var edit = world.Permission();
        var manage = world.Permission();
        var schema = new SchemaBuilder(world.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Permission(edit, p => p.Relation(grant))
                .Permission(manage, p => p.Relation(edit)))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void RelationRef_to_unknown_name_fails_with_message()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var edit = world.Permission();
        var unknown = world.Relation();
        var schema = new SchemaBuilder(world.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Permission(edit, p => p.Relation(unknown)))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(objType) && e.Contains(unknown));
    }

    [Fact]
    public void Duplicate_relation_name_on_a_type_fails()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var dup = world.Relation();
        var schema = new Schema(world.Version,
            [new EntityTypeDef(objType,
                [new RelationDef(dup, [new SubjectTypeRef(world.UserType)]),
                 new RelationDef(dup, [new SubjectTypeRef(world.UserType)])],
                [])],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(dup) && e.Contains("duplicate"));
    }
}
