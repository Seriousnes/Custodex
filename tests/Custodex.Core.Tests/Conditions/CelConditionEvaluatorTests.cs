using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class CelConditionEvaluatorTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _condition;
    private readonly string _attribute;

    public CelConditionEvaluatorTests()
    {
        _condition = _world.ConditionName();
        _attribute = _world.ParamName();
    }

    private ConditionDef CreatorDef() => new(
        _condition,
        [],
        new Compare(new AttributeRef(_attribute), CompareOp.Eq, new ContextSubject()));

    private RequestContext Ctx(string subjectId) =>
        new(DateTimeOffset.UnixEpoch, _world.User(subjectId), new Dictionary<string, object?>());

    [Fact]
    public void Adapter_forwards_to_static_evaluator_satisfied_and_denied()
    {
        var adapter = new CelConditionEvaluator();
        var def = CreatorDef();
        var inv = new ConditionRef(_condition, new Dictionary<string, object?>());
        var creator = _world.SubjectId();
        var other = _world.SubjectId();

        adapter.Evaluate(def, inv,
            new Dictionary<string, object?> { [_attribute] = creator },
            Ctx(creator)).Allowed.ShouldBeTrue();

        adapter.Evaluate(def, inv,
            new Dictionary<string, object?> { [_attribute] = other },
            Ctx(creator)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task End_to_end_conditioned_branch_gates_on_attribute_equals_subject()
    {
        var objType = _world.EntityType();
        var viewer = _world.Relation();
        var view = _world.Permission();
        var objId = _world.ObjectId();
        var subject = _world.SubjectId();
        var nonMatch = _world.SubjectId();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(viewer, s => s.Type(_world.UserType))
                .Permission(view, p => p.Relation(viewer).Conditioned(_condition)))
            .Condition(_condition, p => { }, b => b.Eq(b.Attribute(_attribute), b.Subject()))
            .Build();

        var viewerTuple = TestWorld.Tuple(objType, objId, viewer, _world.User(subject));
        var obj = TestWorld.Object(objType, objId);

        {
            var auth = await _world.BuildAsync(schema, new CelConditionEvaluator(), [viewerTuple],
                [(obj, new Dictionary<string, object?> { [_attribute] = subject })]);
            var req = new CheckRequest(_world.Tenant, obj, view, _world.User(subject), Ctx(subject));
            (await auth.CheckAsync(req)).Allowed.ShouldBeTrue();
        }

        {
            var auth = await _world.BuildAsync(schema, new CelConditionEvaluator(), [viewerTuple],
                [(obj, new Dictionary<string, object?> { [_attribute] = nonMatch })]);
            var req = new CheckRequest(_world.Tenant, obj, view, _world.User(subject), Ctx(subject));
            (await auth.CheckAsync(req)).Allowed.ShouldBeFalse();
        }
    }
}
