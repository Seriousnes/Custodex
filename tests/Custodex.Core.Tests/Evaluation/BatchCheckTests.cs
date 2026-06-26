using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class BatchCheckTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _viewer;
    private readonly string _view;
    private readonly string _objId1;
    private readonly string _objId2;
    private readonly string _groupId;
    private readonly string _viaGroup;
    private readonly string _direct;
    private readonly string _actor;

    public BatchCheckTests()
    {
        _objType = _world.EntityType();
        _viewer = _world.Relation();
        _view = _world.Permission();
        _objId1 = _world.ObjectId();
        _objId2 = _world.ObjectId();
        _groupId = _world.SubjectId();
        _viaGroup = _world.SubjectId();
        _direct = _world.SubjectId();
        _actor = _world.SubjectId();
    }

    private TenantContext T => _world.Tenant;

    private Task<EngineDrivenAuthorizer> NewAsync()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_world.GroupType, t => t.Relation(_world.MemberRelation,
                s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation)))
            .Type(_objType, t => t
                .Relation(_viewer, s => s.Type(_world.UserType).SubjectSet(_world.GroupType, _world.MemberRelation))
                .Permission(_view, p => p.Relation(_viewer)))
            .Build();
        return _world.BuildAsync(schema,
            TestWorld.Tuple(_objType, _objId1, _viewer, _world.Member(_groupId)),
            TestWorld.Tuple(_objType, _objId2, _viewer, _world.User(_direct)),
            TestWorld.Tuple(_world.GroupType, _groupId, _world.MemberRelation, _world.User(_viaGroup)));
    }

    [Fact]
    public async Task Batch_returns_a_result_per_item_in_order()
    {
        var auth = await NewAsync();
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, _world.User(_actor),
            new Dictionary<string, object?>());
        var req = new BatchCheckRequest(T,
        [
            new CheckItem(new EntityRef(_objType, _objId1), _view, _world.User(_viaGroup)),
            new CheckItem(new EntityRef(_objType, _objId1), _view, _world.User(_direct)),
            new CheckItem(new EntityRef(_objType, _objId2), _view, _world.User(_direct)),
        ], ctx);

        var results = await auth.BatchCheckAsync(req);
        results.Count.ShouldBe(3);
        results[0].Allowed.ShouldBeTrue();
        results[1].Allowed.ShouldBeFalse();
        results[2].Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Empty_batch_returns_empty_list()
    {
        var auth = await NewAsync();
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, _world.User(_actor),
            new Dictionary<string, object?>());
        var results = await auth.BatchCheckAsync(new BatchCheckRequest(T, [], ctx));
        results.ShouldBeEmpty();
    }
}
