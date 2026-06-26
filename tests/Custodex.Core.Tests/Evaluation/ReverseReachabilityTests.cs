using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ReverseReachabilityTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _editor;
    private readonly string _edit;

    public ReverseReachabilityTests()
    {
        _objType = _world.EntityType();
        _editor = _world.Relation();
        _edit = _world.Permission();
    }

    private TenantContext T => _world.Tenant;

    private Schema Build() => new SchemaBuilder(_world.Version)
        .Type(_world.GroupType, t => t.Relation(_world.MemberRelation,
            s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation)))
        .Type(_objType, t => t
            .Relation(_editor, s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation))
            .Permission(_edit, p => p.Relation(_editor)))
        .Build();

    private Task<EngineDrivenAuthorizer> NewAsync(params RelationTuple[] tuples) =>
        _world.BuildAsync(Build(), tuples);

    private RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        _world.Tuple(ot, oid, rel, s);

    [Fact]
    public async Task Gathers_objects_reachable_via_direct_and_nested_group_grants()
    {
        var groupId = _world.SubjectId();
        var subject = _world.SubjectId();
        var other = _world.SubjectId();
        var ids = new[] { _world.ObjectId(), _world.ObjectId() }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var unreachable = _world.ObjectId();

        var auth = await NewAsync(
            Tuple(_objType, ids[0], _editor, _world.Member(groupId)),
            Tuple(_objType, ids[1], _editor, _world.Member(groupId)),
            Tuple(_objType, unreachable, _editor, _world.User(other)),
            Tuple(_world.GroupType, groupId, _world.MemberRelation, _world.User(subject)));

        var candidates = await auth.CandidateObjectsForTest(T, _world.User(subject), _objType);
        candidates.Select(c => c.Id).ShouldBe(ids);
    }

    [Fact]
    public async Task Candidate_enumeration_is_cycle_safe()
    {
        var groupA = _world.SubjectId();
        var groupB = _world.SubjectId();
        var subject = _world.SubjectId();
        var objId = _world.ObjectId();

        var auth = await NewAsync(
            Tuple(_objType, objId, _editor, _world.Member(groupA)),
            Tuple(_world.GroupType, groupA, _world.MemberRelation, _world.Member(groupB)),
            Tuple(_world.GroupType, groupB, _world.MemberRelation, _world.Member(groupA)),
            Tuple(_world.GroupType, groupA, _world.MemberRelation, _world.User(subject)));

        var candidates = await auth.CandidateObjectsForTest(T, _world.User(subject), _objType);
        candidates.Select(c => c.Id).ShouldBe([objId]);
    }

    [Fact]
    public async Task Gathers_objects_via_non_group_member_subject_sets()
    {
        var teamType = _world.EntityType();
        var teamRel = _world.Relation();
        var altGroupRel = _world.Relation();
        var teamId = _world.SubjectId();
        var altGroupId = _world.SubjectId();
        var subject = _world.SubjectId();
        var ids = new[] { _world.ObjectId(), _world.ObjectId() }.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var auth = await NewAsync(
            Tuple(_objType, ids[0], _editor, _world.SubjectSet(teamType, teamId, teamRel)),
            Tuple(teamType, teamId, teamRel, _world.User(subject)),
            Tuple(_objType, ids[1], _editor, _world.SubjectSet(_world.GroupType, altGroupId, altGroupRel)),
            Tuple(_world.GroupType, altGroupId, altGroupRel, _world.User(subject)));

        var candidates = await auth.CandidateObjectsForTest(T, _world.User(subject), _objType);
        candidates.Select(c => c.Id).ShouldBe(ids);
    }
}
