using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ListObjectsTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _editor;
    private readonly string _blocked;
    private readonly string _edit;
    private readonly string[] _sortedIds;

    public ListObjectsTests()
    {
        _objType = _world.EntityType();
        _editor = _world.Relation();
        _blocked = _world.Relation();
        _edit = _world.Permission();
        _sortedIds = new[]
        {
            _world.ObjectId(), _world.ObjectId(), _world.ObjectId(), _world.ObjectId(), _world.ObjectId(),
        }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private Schema Build() => new SchemaBuilder(_world.Version)
        .Type(_world.GroupType, t => t.Relation(_world.MemberRelation,
            s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation)))
        .Type(_objType, t => t
            .Relation(_editor, s => s.Type(_world.UserType)
                .SubjectSet(_world.GroupType, _world.MemberRelation).Wildcard(_world.UserType))
            .Relation(_blocked, s => s.Type(_world.UserType))
            .Permission(_edit, p => p.Relation(_editor).Exclude(x => x.Relation(_blocked))))
        .Build();

    private Task<EngineDrivenAuthorizer> NewAsync(params RelationTuple[] tuples) =>
        _world.BuildAsync(Build(), tuples);

    private RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        _world.Tuple(ot, oid, rel, s);

    private ListObjectsRequest Req(string user, int pageSize = 100, string? token = null) => new(
        _world.Tenant, _world.User(user), _objType, _edit,
        new RequestContext(DateTimeOffset.UnixEpoch, _world.User(user),
            new Dictionary<string, object?>()), pageSize, token);

    [Fact]
    public async Task Lists_only_confirmed_objects_respecting_exclusion()
    {
        var groupId = _world.SubjectId();
        var subjectId = _world.SubjectId();
        var allowedObj = _world.ObjectId();
        var revokedObj = _world.ObjectId();
        var auth = await NewAsync(
            Tuple(_objType, allowedObj, _editor, _world.Member(groupId)),
            Tuple(_objType, revokedObj, _editor, _world.Member(groupId)),
            Tuple(_objType, revokedObj, _blocked, _world.User(subjectId)),
            Tuple(_world.GroupType, groupId, _world.MemberRelation, _world.User(subjectId)));

        var result = await auth.ListObjectsAsync(Req(subjectId));
        result.ObjectIds.ShouldBe(new[] { allowedObj });
        result.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_lists_every_object_of_the_type()
    {
        var anyone = _world.SubjectId();
        var ids = _sortedIds.Take(3).ToArray();
        var auth = await NewAsync(
            Tuple(_objType, ids[0], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[1], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[2], _editor, new SubjectRef(_world.UserType, "*")));

        var result = await auth.ListObjectsAsync(Req(anyone));
        result.ObjectIds.ShouldBe(ids);
    }

    [Fact]
    public async Task Paginates_to_exact_page_size_with_resumable_cursor()
    {
        var anyone = _world.SubjectId();
        var ids = _sortedIds;
        var auth = await NewAsync(
            Tuple(_objType, ids[0], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[1], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[2], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[3], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[4], _editor, new SubjectRef(_world.UserType, "*")));

        var page1 = await auth.ListObjectsAsync(Req(anyone, pageSize: 2));
        page1.ObjectIds.ShouldBe(new[] { ids[0], ids[1] });
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListObjectsAsync(Req(anyone, pageSize: 2, token: page1.ContinuationToken));
        page2.ObjectIds.ShouldBe(new[] { ids[2], ids[3] });
        page2.ContinuationToken.ShouldNotBeNull();

        var page3 = await auth.ListObjectsAsync(Req(anyone, pageSize: 2, token: page2.ContinuationToken));
        page3.ObjectIds.ShouldBe(new[] { ids[4] });
        page3.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Pages_do_not_overlap_or_drop_across_the_full_range()
    {
        var anyone = _world.SubjectId();
        var ids = _sortedIds.Take(3).ToArray();
        var auth = await NewAsync(
            Tuple(_objType, ids[0], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[1], _editor, new SubjectRef(_world.UserType, "*")),
            Tuple(_objType, ids[2], _editor, new SubjectRef(_world.UserType, "*")));

        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await auth.ListObjectsAsync(Req(anyone, pageSize: 2, token: token));
            all.AddRange(page.ObjectIds);
            token = page.ContinuationToken;
        } while (token is not null);

        all.ShouldBe(ids);
    }
}
