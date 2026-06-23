using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Evaluation;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Evaluation;

public class SchemaIndexTests
{
    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("animal", t => t
            .Relation("medicator", s => s.User().SubjectSet("group", "member"))
            .Relation("enclosure", s => s.Type("enclosure"))
            .Permission("edit", p => p.Relation("medicator")))
        .Build();

    [Fact]
    public void Resolves_known_type_relation_and_permission()
    {
        var idx = new SchemaIndex(Build());
        idx.Type("animal").Name.ShouldBe("animal");
        idx.Relation("animal", "enclosure").Name.ShouldBe("enclosure");
        idx.Permission("animal", "edit").Name.ShouldBe("edit");
    }

    [Fact]
    public void Unknown_lookups_throw_typed_exceptions()
    {
        var idx = new SchemaIndex(Build());
        Should.Throw<UnknownTypeException>(() => idx.Type("dragon"));
        Should.Throw<UnknownRelationException>(() => idx.Relation("animal", "nope"));
        Should.Throw<UnknownPermissionException>(() => idx.Permission("animal", "nope"));
    }

    [Fact]
    public void TryPermission_distinguishes_permission_from_relation()
    {
        var idx = new SchemaIndex(Build());
        idx.TryPermission("animal", "edit", out _).ShouldBeTrue();
        idx.TryPermission("animal", "enclosure", out _).ShouldBeFalse();   // a relation, not a permission
    }
}
