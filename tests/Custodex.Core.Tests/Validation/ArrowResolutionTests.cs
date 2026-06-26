using Custodex.Abstractions;
using Custodex.Core.Validation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Validation;

public class ArrowResolutionTests
{
    [Fact]
    public void Arrow_to_permission_present_on_related_type_passes()
    {
        var world = TestWorld.New();
        var linkedType = world.EntityType();
        var childType = world.EntityType();
        var canEdit = world.Relation();
        var link = world.Relation();
        var edit = world.Permission();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(linkedType, t => t
                .Relation(canEdit, s => s.Type(world.UserType))
                .Permission(edit, p => p.Relation(canEdit)))
            .Type(childType, t => t
                .Relation(link, s => s.Type(linkedType))
                .Permission(edit, p => p.Arrow(link, edit)))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Arrow_to_permission_absent_on_related_type_fails()
    {
        var world = TestWorld.New();
        var linkedType = world.EntityType();
        var childType = world.EntityType();
        var canEdit = world.Relation();
        var link = world.Relation();
        var edit = world.Permission();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(linkedType, t => t.Relation(canEdit, s => s.Type(world.UserType)))
            .Type(childType, t => t
                .Relation(link, s => s.Type(linkedType))
                .Permission(edit, p => p.Arrow(link, edit)))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(linkedType) && e.Contains(edit));
    }

    [Fact]
    public void Arrow_fails_when_one_of_several_target_types_lacks_the_permission()
    {
        var world = TestWorld.New();
        var hasPermType = world.EntityType();
        var lacksPermType = world.EntityType();
        var childType = world.EntityType();
        var canEdit = world.Relation();
        var parent = world.Relation();
        var edit = world.Permission();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(hasPermType, t => t
                .Relation(canEdit, s => s.Type(world.UserType))
                .Permission(edit, p => p.Relation(canEdit)))
            .Type(lacksPermType, t => t.Relation(canEdit, s => s.Type(world.UserType)))
            .Type(childType, t => t
                .Relation(parent, s => s.Type(hasPermType).Type(lacksPermType))
                .Permission(edit, p => p.Arrow(parent, edit)))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(lacksPermType) && e.Contains(edit));
    }

    [Fact]
    public void Arrow_to_unknown_related_type_fails()
    {
        var world = TestWorld.New();
        var childType = world.EntityType();
        var linkedType = world.EntityType();
        var link = world.Relation();
        var edit = world.Permission();

        var schema = new Schema(TestWorld.Version,
            [new EntityTypeDef(childType,
                [new RelationDef(link, [new SubjectTypeRef(linkedType)])],
                [new PermissionDef(edit, new Arrow(link, edit))])],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(linkedType));
    }
}
