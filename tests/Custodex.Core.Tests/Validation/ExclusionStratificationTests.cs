using Custodex.Core.Validation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Validation;

public class ExclusionStratificationTests
{
    [Fact]
    public void Excluded_branch_arrowing_back_to_the_same_permission_is_rejected()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var link = world.Relation();
        var view = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(link, s => s.Type(objType))
                .Permission(view, p => p.Relation(grant).Exclude(x => x.Arrow(link, view))))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("exclusion") && e.Contains($"{objType}.{view}"));
    }

    [Fact]
    public void Cross_type_cycle_through_a_negated_arrow_is_rejected()
    {
        var world = TestWorld.New();
        var typeA = world.EntityType();
        var typeB = world.EntityType();
        var grantA = world.Relation();
        var grantB = world.Relation();
        var linkAtoB = world.Relation();
        var linkBtoA = world.Relation();
        var permA = world.Permission();
        var permB = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(typeA, t => t
                .Relation(grantA, s => s.Type(world.UserType))
                .Relation(linkAtoB, s => s.Type(typeB))
                .Permission(permA, p => p.Relation(grantA).Exclude(x => x.Arrow(linkAtoB, permB))))
            .Type(typeB, t => t
                .Relation(grantB, s => s.Type(world.UserType))
                .Relation(linkBtoA, s => s.Type(typeA))
                .Permission(permB, p => p.Relation(grantB).Union(x => x.Arrow(linkBtoA, permA))))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("exclusion") && e.Contains($"{typeA}.{permA}"));
    }

    [Fact]
    public void Excluded_branch_arrowing_into_a_self_recursive_lower_stratum_is_valid()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var holderType = world.EntityType();
        var grant = world.Relation();
        var mark = world.Relation();
        var home = world.Relation();
        var parent = world.Relation();
        var view = world.Permission();
        var marked = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(holderType, t => t
                .Relation(mark, s => s.Type(world.UserType))
                .Relation(parent, s => s.Type(holderType))
                .Permission(marked, p => p.Relation(mark).Union(x => x.Arrow(parent, marked))))
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(home, s => s.Type(holderType))
                .Permission(view, p => p.Relation(grant).Exclude(x => x.Arrow(home, marked))))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Recursion_on_the_kept_side_of_an_exclusion_is_valid()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var parent = world.Relation();
        var blocked = world.Relation();
        var view = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(parent, s => s.Type(objType))
                .Relation(blocked, s => s.Type(world.UserType))
                .Permission(view, p => p
                    .Relation(grant).Union(x => x.Arrow(parent, view))
                    .Exclude(x => x.Relation(blocked))))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Cycle_under_a_doubly_nested_exclusion_is_rejected()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var grant = world.Relation();
        var gate = world.Relation();
        var link = world.Relation();
        var view = world.Permission();
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(world.UserType))
                .Relation(gate, s => s.Type(world.UserType))
                .Relation(link, s => s.Type(objType))
                .Permission(view, p => p.Relation(grant)
                    .Exclude(x => x.Relation(gate).Exclude(y => y.Arrow(link, view)))))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("exclusion") && e.Contains($"{objType}.{view}"));
    }
}
