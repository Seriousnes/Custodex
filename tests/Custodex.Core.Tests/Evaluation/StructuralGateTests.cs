using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

/// <summary>
/// Exercises the engine's hardest composite shape: a base grant revoked by an arrow-gated exclusion,
/// unioned with an arrow-gated intersection of group memberships. Verifies that nested
/// exclusion/intersection through arrows resolves pointwise.
/// </summary>
public class StructuralGateTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _groupType;
    private readonly string _childType;
    private readonly string _parentType;
    private readonly string _gate;          // relation + permission on the parent (the arrow target)
    private readonly string _baseRel;       // direct base grant on the child
    private readonly string _link;          // child -> parent relation
    private readonly string _setA;          // first intersection branch (union of A1/A2)
    private readonly string _setA2;
    private readonly string _setB;          // second intersection branch
    private readonly string _access;
    private readonly string _groupA;        // members of setA and setB groups
    private readonly string _groupB;
    private readonly string _bothSubject;   // in setA and setB
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

    private Schema Build() => new SchemaBuilder(_world.Version)
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
        _world.Check(_world.Object(_childType, childId), _access, _world.User(subjectId));

    // Common membership wiring: _bothSubject is in setA and setB; _setAOnlySubject is in setA only.
    private RelationTuple[] Members(string childId) =>
    [
        _world.Tuple(_childType, childId, _setA, _world.Member(_groupA)),
        _world.Tuple(_childType, childId, _setB, _world.Member(_groupB)),
        _world.Tuple(_groupType, _groupA, _world.MemberRelation, _world.User(_bothSubject)),
        _world.Tuple(_groupType, _groupA, _world.MemberRelation, _world.User(_setAOnlySubject)),
        _world.Tuple(_groupType, _groupB, _world.MemberRelation, _world.User(_bothSubject)),
        _world.Tuple(_childType, childId, _baseRel, _world.User(_bothSubject)),
        _world.Tuple(_childType, childId, _baseRel, _world.User(_setAOnlySubject)),
    ];

    [Fact]
    public async Task Subject_in_both_sets_inside_the_gate_is_allowed()
    {
        var childId = _world.ObjectId();
        var parentId = _world.ObjectId();
        var tuples = new List<RelationTuple>(Members(childId))
        {
            _world.Tuple(_childType, childId, _link, new SubjectRef(_parentType, parentId)),
            _world.Tuple(_parentType, parentId, _gate, new SubjectRef(_world.UserType, "*")),
        };
        var auth = await _world.BuildAsync(Build(), tuples.ToArray());
        (await auth.CheckAsync(Access(childId, _bothSubject))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Subject_missing_a_set_inside_the_gate_is_denied()
    {
        var childId = _world.ObjectId();
        var parentId = _world.ObjectId();
        var tuples = new List<RelationTuple>(Members(childId))
        {
            _world.Tuple(_childType, childId, _link, new SubjectRef(_parentType, parentId)),
            _world.Tuple(_parentType, parentId, _gate, new SubjectRef(_world.UserType, "*")),
        };
        var auth = await _world.BuildAsync(Build(), tuples.ToArray());
        // _setAOnlySubject has the base grant, but base is revoked inside the gate and it is missing setB.
        (await auth.CheckAsync(Access(childId, _setAOnlySubject))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Outside_the_gate_base_access_passes_through()
    {
        // The child is linked to a parent with NO gate wildcard tuple.
        var childId = _world.ObjectId();
        var parentId = _world.ObjectId();
        var tuples = new List<RelationTuple>(Members(childId))
        {
            _world.Tuple(_childType, childId, _link, new SubjectRef(_parentType, parentId)),
        };
        var auth = await _world.BuildAsync(Build(), tuples.ToArray());
        (await auth.CheckAsync(Access(childId, _setAOnlySubject))).Allowed.ShouldBeTrue();   // base access intact
        (await auth.CheckAsync(Access(childId, _bothSubject))).Allowed.ShouldBeTrue();
    }
}
