using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Validation;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Validation;

public class ArrowResolutionTests
{
    [Fact]
    public void Arrow_to_permission_present_on_related_type_passes()
    {
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("can_edit", s => s.User())
                .Permission("edit", p => p.Relation("can_edit")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Arrow_to_permission_absent_on_related_type_fails()
    {
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t.Relation("can_edit", s => s.User()))   // no 'edit' permission
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("enclosure") && e.Contains("edit"));
    }

    [Fact]
    public void Arrow_fails_when_one_of_several_target_types_lacks_the_permission()
    {
        // 'parent' may be an enclosure (has edit) or a site (lacks edit).
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("can_edit", s => s.User())
                .Permission("edit", p => p.Relation("can_edit")))
            .Type("site", t => t.Relation("can_edit", s => s.User()))   // no 'edit' permission
            .Type("animal", t => t
                .Relation("parent", s => s.Type("enclosure").Type("site"))
                .Permission("edit", p => p.Arrow("parent", "edit")))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("site") && e.Contains("edit"));
    }

    [Fact]
    public void Arrow_to_unknown_related_type_fails()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("animal",
                [new RelationDef("enclosure", [new SubjectTypeRef("enclosure")])],
                [new PermissionDef("edit", new Arrow("enclosure", "edit"))])],
            []);   // no 'enclosure' type declared at all

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("enclosure"));
    }
}
