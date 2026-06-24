using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class SchemaIndexTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _grantRel;
    private readonly string _linkRel;
    private readonly string _linkedType;
    private readonly string _edit;

    public SchemaIndexTests()
    {
        _objType = _world.EntityType();
        _grantRel = _world.Relation();
        _linkRel = _world.Relation();
        _linkedType = _world.EntityType();
        _edit = _world.Permission();
    }

    private Schema Build() => new SchemaBuilder(_world.Version)
        .Type(_world.GroupType, t => t.Relation(_world.MemberRelation,
            s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation)))
        .Type(_objType, t => t
            .Relation(_grantRel, s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation))
            .Relation(_linkRel, s => s.Type(_linkedType))
            .Permission(_edit, p => p.Relation(_grantRel)))
        .Build();

    [Fact]
    public void Resolves_known_type_relation_and_permission()
    {
        var idx = new SchemaIndex(Build());
        idx.Type(_objType).Name.ShouldBe(_objType);
        idx.Relation(_objType, _linkRel).Name.ShouldBe(_linkRel);
        idx.Permission(_objType, _edit).Name.ShouldBe(_edit);
    }

    [Fact]
    public void Unknown_lookups_throw_typed_exceptions()
    {
        var idx = new SchemaIndex(Build());
        var unknownType = _world.EntityType();
        var unknownName = _world.Relation();
        Should.Throw<UnknownTypeException>(() => idx.Type(unknownType));
        Should.Throw<UnknownRelationException>(() => idx.Relation(_objType, unknownName));
        Should.Throw<UnknownPermissionException>(() => idx.Permission(_objType, unknownName));
    }

    [Fact]
    public void TryPermission_distinguishes_permission_from_relation()
    {
        var idx = new SchemaIndex(Build());
        idx.TryPermission(_objType, _edit, out _).ShouldBeTrue();
        idx.TryPermission(_objType, _linkRel, out _).ShouldBeFalse();
    }
}
