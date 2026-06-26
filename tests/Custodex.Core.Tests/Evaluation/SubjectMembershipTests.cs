using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class SubjectMembershipTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _viewer;
    private readonly string _view;
    private readonly string _objId;

    public SubjectMembershipTests()
    {
        _objType = _world.EntityType();
        _viewer = _world.Relation();
        _view = _world.Permission();
        _objId = _world.ObjectId();
    }

    private Schema GroupSchema() => new SchemaBuilder(_world.Version)
        .Type(_world.GroupType, t => t.Relation(_world.MemberRelation,
            s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation)))
        .Type(_objType, t => t
            .Relation(_viewer, s => s.Type(_world.UserType)
                .SubjectSet(_world.GroupType, _world.MemberRelation).Wildcard(_world.UserType))
            .Permission(_view, p => p.Relation(_viewer)))
        .Build();

    private Task<EngineDrivenAuthorizer> NewAsync(Schema schema, params RelationTuple[] tuples) =>
        _world.BuildAsync(schema, tuples);

    private CheckRequest Req(string subjectId, string? perm = null) =>
        _world.Check(_world.Object(_objType, _objId), perm ?? _view, _world.User(subjectId));

    [Fact]
    public async Task Direct_user_grant_matches()
    {
        var granted = _world.SubjectId();
        var denied = _world.SubjectId();
        var auth = await NewAsync(GroupSchema(),
            _world.Tuple(_objType, _objId, _viewer, _world.User(granted)));
        (await auth.CheckAsync(Req(granted))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(denied))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Wildcard_grant_matches_everyone()
    {
        var anyone = _world.SubjectId();
        var auth = await NewAsync(GroupSchema(),
            _world.Tuple(_objType, _objId, _viewer, new SubjectRef(_world.UserType, "*")));
        (await auth.CheckAsync(Req(anyone))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Subject_set_grant_matches_via_group_membership()
    {
        var groupId = _world.SubjectId();
        var member = _world.SubjectId();
        var outsider = _world.SubjectId();
        var auth = await NewAsync(GroupSchema(),
            _world.Tuple(_objType, _objId, _viewer, _world.Member(groupId)),
            _world.Tuple(_world.GroupType, groupId, _world.MemberRelation, _world.User(member)));
        (await auth.CheckAsync(Req(member))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(outsider))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Nested_group_membership_resolves_transitively()
    {
        var outerGroup = _world.SubjectId();
        var innerGroup = _world.SubjectId();
        var member = _world.SubjectId();
        var auth = await NewAsync(GroupSchema(),
            _world.Tuple(_objType, _objId, _viewer, _world.Member(outerGroup)),
            _world.Tuple(_world.GroupType, outerGroup, _world.MemberRelation, _world.Member(innerGroup)),
            _world.Tuple(_world.GroupType, innerGroup, _world.MemberRelation, _world.User(member)));
        (await auth.CheckAsync(Req(member))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Group_membership_cycle_prunes_to_deny_without_throwing()
    {
        var groupA = _world.SubjectId();
        var groupB = _world.SubjectId();
        var ghost = _world.SubjectId();
        var auth = await NewAsync(GroupSchema(),
            _world.Tuple(_objType, _objId, _viewer, _world.Member(groupA)),
            _world.Tuple(_world.GroupType, groupA, _world.MemberRelation, _world.Member(groupB)),
            _world.Tuple(_world.GroupType, groupB, _world.MemberRelation, _world.Member(groupA)));
        (await auth.CheckAsync(Req(ghost))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Permission_backed_by_a_same_named_relation_resolves_to_allow()
    {
        var objType = _world.EntityType();
        var objId = _world.ObjectId();
        var view = _world.Permission();
        var granted = _world.SubjectId();
        var denied = _world.SubjectId();
        var schema = new SchemaBuilder(_world.Version)
            .Type(objType, t => t
                .Relation(view, s => s.Type(_world.UserType))
                .Permission(view, p => p.Relation(view)))
            .Build();
        var auth = await NewAsync(schema,
            _world.Tuple(objType, objId, view, _world.User(granted)));
        (await auth.CheckAsync(_world.Check(_world.Object(objType, objId), view, _world.User(granted)))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(_world.Check(_world.Object(objType, objId), view, _world.User(denied)))).Allowed.ShouldBeFalse();
    }
}
