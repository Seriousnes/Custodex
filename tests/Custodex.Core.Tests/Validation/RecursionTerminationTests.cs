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
        var schema = new Schema(TestWorld.Version,
            [new EntityTypeDef(objType,
                [],
                [new PermissionDef(edit, new RelationRef(edit))])],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle") && e.Contains($"{objType}.{edit}"));
    }

    [Fact]
    public void Recursion_through_an_arrow_is_allowed()
    {
        var world = TestWorld.New();
        var typeA = world.EntityType();
        var typeB = world.EntityType();
        var linkAtoB = world.Relation();
        var linkBtoA = world.Relation();
        var edit = world.Permission();
        var schema = new Schema(TestWorld.Version,
            [
                new EntityTypeDef(typeA,
                    [new RelationDef(linkAtoB, [new SubjectTypeRef(typeB)])],
                    [new PermissionDef(edit, new Arrow(linkAtoB, edit))]),
                new EntityTypeDef(typeB,
                    [new RelationDef(linkBtoA, [new SubjectTypeRef(typeA)])],
                    [new PermissionDef(edit, new Arrow(linkBtoA, edit))]),
            ],
            []);

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Self_type_recursive_arrow_is_allowed()
    {
        var world = TestWorld.New();
        var folder = world.EntityType();
        var owner = world.Relation();
        var parent = world.Relation();
        var view = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(folder, t => t
                .Relation(owner, s => s.Type(world.UserType))
                .Relation(parent, s => s.Type(folder))
                .Permission(view, p => p.Relation(owner).Union(x => x.Arrow(parent, view))))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Nested_permissions_without_a_cycle_terminate()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var edit = world.Permission();
        var manage = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Permission(edit, p => p.Relation(grant))
                .Permission(manage, p => p.Relation(edit)))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Subject_set_self_reference_is_not_a_permission_cycle()
    {
        var world = TestWorld.New();
        var read = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
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
        var world = TestWorld.New();
        var objType = world.EntityType();
        var gate = world.Relation();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(gate, s => s.Wildcard(world.UserType))
                .Permission(gate, p => p.Relation(gate)))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
