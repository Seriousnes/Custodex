using Custodex.Abstractions;
using Custodex.Core.Validation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Validation;

public class RecursionTerminationTests
{
    [Fact]
    public void Self_referential_permission_is_a_cycle()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var edit = world.Permission();
        var schema = new Schema(world.Version,
            [new EntityTypeDef(objType,
                [],
                [new PermissionDef(edit, new RelationRef(edit))])],   // edit -> edit
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle") && e.Contains($"{objType}.{edit}"));
    }

    [Fact]
    public void Permission_cycle_through_an_arrow_is_detected()
    {
        // typeA.edit -> typeB.edit -> typeA.edit  (via back-arrows)
        var world = TestWorld.New();
        var typeA = world.EntityType();
        var typeB = world.EntityType();
        var linkAtoB = world.Relation();
        var linkBtoA = world.Relation();
        var edit = world.Permission();
        var schema = new Schema(world.Version,
            [
                new EntityTypeDef(typeA,
                    [new RelationDef(linkAtoB, [new SubjectTypeRef(typeB)])],
                    [new PermissionDef(edit, new Arrow(linkAtoB, edit))]),
                new EntityTypeDef(typeB,
                    [new RelationDef(linkBtoA, [new SubjectTypeRef(typeA)])],
                    [new PermissionDef(edit, new Arrow(linkBtoA, edit))]),
            ],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle"));
    }

    [Fact]
    public void Nested_permissions_without_a_cycle_terminate()
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
                .Permission(manage, p => p.Relation(edit)))   // manage -> edit -> grant (relation, stops)
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Subject_set_self_reference_is_not_a_permission_cycle()
    {
        // group.member fills with group#member (nesting) — a relation filler, not a permission edge.
        var world = TestWorld.New();
        var read = world.Permission();
        var schema = new SchemaBuilder(world.Version)
            .Type(world.GroupType, t => t
                .Relation(world.MemberRelation,
                    s => s.Type(world.UserType).SubjectSet(world.GroupType, world.MemberRelation))
                .Permission(read, p => p.Relation(world.MemberRelation)))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Permission_referencing_a_same_named_relation_is_not_a_cycle()
    {
        // A type has a relation 'gate' AND a permission 'gate' = RelationRef("gate").
        // The authorizer resolves the RelationRef as the RELATION (it never recurses into a same-named
        // permission), so this is NOT a permission self-cycle and must validate.
        var world = TestWorld.New();
        var objType = world.EntityType();
        var gate = world.Relation();   // serves as both the relation and the permission name
        var schema = new SchemaBuilder(world.Version)
            .Type(objType, t => t
                .Relation(gate, s => s.Wildcard(world.UserType))
                .Permission(gate, p => p.Relation(gate)))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
