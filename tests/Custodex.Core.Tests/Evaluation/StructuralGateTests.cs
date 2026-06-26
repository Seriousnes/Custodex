using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class StructuralGateTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _groupType;
    private readonly string _childType;
    private readonly string _parentType;
    private readonly string _gate;
    private readonly string _baseRel;
    private readonly string _link;
    private readonly string _setA;
    private readonly string _setA2;
    private readonly string _setB;
    private readonly string _access;
    private readonly string _groupA;
    private readonly string _groupB;
    private readonly string _bothSubject;
    private readonly string _setAOnlySubject;

    public StructuralGateTests()
    {
        _groupType = _world.GroupType;
        _childType = _world.EntityType();
        _parentType = _world.EntityType();
        _gate = _world.Relation();
        _baseRel = _world.Relation();
        _link = _world.Relation();
        _setA = _world.Relation();
        _setA2 = _world.Relation();
        _setB = _world.Relation();
        _access = _world.Permission();
        _groupA = _world.SubjectId();
        _groupB = _world.SubjectId();
        _bothSubject = _world.SubjectId();
        _setAOnlySubject = _world.SubjectId();
    }

    private Schema Build() => new SchemaBuilder(TestWorld.Version)
        .Type(_groupType, t => t.Relation(_world.MemberRelation,
            s => s.Type(_world.UserType).SubjectSet(_groupType, _world.MemberRelation)))
        .Type(_parentType, t => t
            .Relation(_gate, s => s.Wildcard(_world.UserType))
            .Permission(_gate, p => p.Relation(_gate)))
        .Type(_childType, t => t
            .Relation(_baseRel, s => s.Type(_world.UserType).SubjectSet(_groupType, _world.MemberRelation))
            .Relation(_link, s => s.Type(_parentType))
            .Relation(_setA, s => s.SubjectSet(_groupType, _world.MemberRelation))
            .Relation(_setA2, s => s.SubjectSet(_groupType, _world.MemberRelation))
            .Relation(_setB, s => s.SubjectSet(_groupType, _world.MemberRelation))
            .Permission(_access, p => p
                .Union(b => b.Relation(_baseRel).Exclude(x => x.Arrow(_link, _gate)))
                .Union(b => b
                    .Arrow(_link, _gate)
                    .Intersect(x => x.Relation(_setA).Union(y => y.Relation(_setA2)))
                    .Intersect(x => x.Relation(_setB)))))
        .Build();

    private CheckRequest Access(string childId, string subjectId) =>
        _world.Check(TestWorld.Object(_childType, childId), _access, _world.User(subjectId));

    private RelationTuple[] Members(string childId) =>
    [
        TestWorld.Tuple(_childType, childId, _setA, _world.Member(_groupA)),
        TestWorld.Tuple(_childType, childId, _setB, _world.Member(_groupB)),
        TestWorld.Tuple(_groupType, _groupA, _world.MemberRelation, _world.User(_bothSubject)),
        TestWorld.Tuple(_groupType, _groupA, _world.MemberRelation, _world.User(_setAOnlySubject)),
        TestWorld.Tuple(_groupType, _groupB, _world.MemberRelation, _world.User(_bothSubject)),
        TestWorld.Tuple(_childType, childId, _baseRel, _world.User(_bothSubject)),
        TestWorld.Tuple(_childType, childId, _baseRel, _world.User(_setAOnlySubject)),
    ];

    [Fact]
    public async Task Subject_in_both_sets_inside_the_gate_is_allowed()
    {
        var childId = _world.ObjectId();
        var parentId = _world.ObjectId();
        var tuples = new List<RelationTuple>(Members(childId))
        {
            TestWorld.Tuple(_childType, childId, _link, new SubjectRef(_parentType, parentId)),
            TestWorld.Tuple(_parentType, parentId, _gate, new SubjectRef(_world.UserType, "*")),
        };
        var auth = await _world.BuildAsync(Build(), [.. tuples]);
        (await auth.CheckAsync(Access(childId, _bothSubject))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Subject_missing_a_set_inside_the_gate_is_denied()
    {
        var childId = _world.ObjectId();
        var parentId = _world.ObjectId();
        var tuples = new List<RelationTuple>(Members(childId))
        {
            TestWorld.Tuple(_childType, childId, _link, new SubjectRef(_parentType, parentId)),
            TestWorld.Tuple(_parentType, parentId, _gate, new SubjectRef(_world.UserType, "*")),
        };
        var auth = await _world.BuildAsync(Build(), [.. tuples]);
        (await auth.CheckAsync(Access(childId, _setAOnlySubject))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Outside_the_gate_base_access_passes_through()
    {
        var childId = _world.ObjectId();
        var parentId = _world.ObjectId();
        var tuples = new List<RelationTuple>(Members(childId))
        {
            TestWorld.Tuple(_childType, childId, _link, new SubjectRef(_parentType, parentId)),
        };
        var auth = await _world.BuildAsync(Build(), [.. tuples]);
        (await auth.CheckAsync(Access(childId, _setAOnlySubject))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Access(childId, _bothSubject))).Allowed.ShouldBeTrue();
    }
}
