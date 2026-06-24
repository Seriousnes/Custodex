using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ListSubjectsTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _viewer;
    private readonly string _blocked;
    private readonly string _view;
    private readonly string _objId;
    private readonly string _actor;

    public ListSubjectsTests()
    {
        _objType = _world.EntityType();
        _viewer = _world.Relation();
        _blocked = _world.Relation();
        _view = _world.Permission();
        _objId = _world.ObjectId();
        _actor = _world.SubjectId();
    }

    private SubjectRef User(string id) => _world.User(id);

    private Schema Build() => new SchemaBuilder(_world.Version)
        .Type(_world.GroupType, t => t.Relation(_world.MemberRelation,
            s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation)))
        .Type(_objType, t => t
            .Relation(_viewer, s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation))
            .Relation(_blocked, s => s.Type(_world.UserType))
            .Permission(_view, p => p.Relation(_viewer).Exclude(x => x.Relation(_blocked))))
        .Build();

    private Task<EngineDrivenAuthorizer> NewAsync(params RelationTuple[] tuples) =>
        _world.BuildAsync(Build(), tuples);

    private RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        _world.Tuple(ot, oid, rel, s);

    private ListSubjectsRequest Req(int pageSize = 100, string? token = null) => new(
        _world.Tenant, new EntityRef(_objType, _objId), _view,
        new RequestContext(DateTimeOffset.UnixEpoch, User(_actor),
            new Dictionary<string, object?>()), pageSize, token);

    private string[] SortedSubjects(int count)
    {
        var ids = new string[count];
        for (var i = 0; i < count; i++) ids[i] = _world.SubjectId();
        return ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Lists_leaf_users_via_nested_groups_honouring_exclusion()
    {
        var groupId = _world.SubjectId();
        var allowed = _world.SubjectId();
        var revoked = _world.SubjectId();
        var auth = await NewAsync(
            Tuple(_objType, _objId, _viewer, _world.Member(groupId)),
            Tuple(_world.GroupType, groupId, _world.MemberRelation, User(allowed)),
            Tuple(_world.GroupType, groupId, _world.MemberRelation, User(revoked)),
            Tuple(_objType, _objId, _blocked, User(revoked)));

        var result = await auth.ListSubjectsAsync(Req());
        result.Subjects.Select(s => s.Id).ShouldBe(new[] { allowed });
    }

    [Fact]
    public async Task Paginates_subjects_to_exact_page_size()
    {
        var groupId = _world.SubjectId();
        var ids = SortedSubjects(3);
        var auth = await NewAsync(
            Tuple(_objType, _objId, _viewer, _world.Member(groupId)),
            Tuple(_world.GroupType, groupId, _world.MemberRelation, User(ids[0])),
            Tuple(_world.GroupType, groupId, _world.MemberRelation, User(ids[1])),
            Tuple(_world.GroupType, groupId, _world.MemberRelation, User(ids[2])));

        var page1 = await auth.ListSubjectsAsync(Req(pageSize: 2));
        page1.Subjects.Select(s => s.Id).ShouldBe(new[] { ids[0], ids[1] });
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListSubjectsAsync(Req(pageSize: 2, token: page1.ContinuationToken));
        page2.Subjects.Select(s => s.Id).ShouldBe(new[] { ids[2] });
        page2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_surfaces_as_star_and_respects_page_size()
    {
        var ids = SortedSubjects(2);
        var auth = await NewAsync(
            Tuple(_objType, _objId, _viewer, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, _objId, _viewer, User(ids[0])),
            Tuple(_objType, _objId, _viewer, User(ids[1])));

        var page1 = await auth.ListSubjectsAsync(Req(pageSize: 2));
        page1.Subjects.Count.ShouldBe(2);
        page1.Subjects.Select(s => s.Id).ShouldBe(new[] { "*", ids[0] });
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListSubjectsAsync(Req(pageSize: 2, token: page1.ContinuationToken));
        page2.Subjects.Select(s => s.Id).ShouldBe(new[] { ids[1] });
        page2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Lists_subjects_of_any_principal_type()
    {
        var typeA = _world.UserType;
        var typeB = _world.EntityType();
        var idA = _world.SubjectId();
        var idB = _world.SubjectId();
        var grant = _world.Relation();
        var perm = _world.Permission();
        var objType = _world.EntityType();
        var objId = _world.ObjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(objType, t => t
                .Relation(grant, s => s.Type(typeA).Type(typeB))
                .Permission(perm, p => p.Relation(grant)))
            .Build();
        var auth = await _world.BuildAsync(schema,
            _world.Tuple(objType, objId, grant, _world.Subject(typeA, idA)),
            _world.Tuple(objType, objId, grant, _world.Subject(typeB, idB)));

        var result = await auth.ListSubjectsAsync(new ListSubjectsRequest(
            _world.Tenant, new EntityRef(objType, objId), perm,
            new RequestContext(DateTimeOffset.UnixEpoch, _world.Subject(typeA, idA),
                new Dictionary<string, object?>())));

        result.Subjects.ShouldBe(
            new[] { _world.Subject(typeA, idA), _world.Subject(typeB, idB) }, ignoreOrder: true);
    }

    [Fact]
    public async Task Cyclic_group_membership_does_not_hang_collection()
    {
        var groupA = _world.SubjectId();
        var groupB = _world.SubjectId();
        var subject = _world.SubjectId();
        var auth = await NewAsync(
            Tuple(_objType, _objId, _viewer, _world.Member(groupA)),
            Tuple(_world.GroupType, groupA, _world.MemberRelation, _world.Member(groupB)),
            Tuple(_world.GroupType, groupB, _world.MemberRelation, _world.Member(groupA)),
            Tuple(_world.GroupType, groupA, _world.MemberRelation, User(subject)));

        var result = await auth.ListSubjectsAsync(Req());
        result.Subjects.Select(s => s.Id).ShouldBe(new[] { subject });
    }
}
