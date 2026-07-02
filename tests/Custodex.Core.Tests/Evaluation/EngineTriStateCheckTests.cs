using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class EngineTriStateCheckTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _doc;
    private readonly string _viewer;
    private readonly string _blocked;
    private readonly string _other;
    private readonly string _view;
    private readonly string _active;
    private readonly string _flag;

    public EngineTriStateCheckTests()
    {
        _doc = _world.EntityType();
        _viewer = _world.Relation();
        _blocked = _world.Relation();
        _other = _world.Relation();
        _view = _world.Permission();
        _active = _world.ConditionName();
        _flag = _world.ParamName();
    }

    private Task<Custodex.Core.Evaluation.EngineDrivenAuthorizer> BuildAsync(
        Schema schema, IReadOnlyList<RelationTuple> tuples,
        IReadOnlyList<(EntityRef, IReadOnlyDictionary<string, object?>)>? attrs = null) =>
        _world.BuildAsync(schema, new CelConditionEvaluator(), tuples, attrs);

    private EntityRef Doc(string id) => new(_doc, id);

    [Fact]
    public async Task Direct_conditional_grant_with_absent_attribute_is_conditional()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Permission(_view, p => p.Relation(_viewer).Conditioned(_active)))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema, [TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1"))]);

        var result = await auth.CheckAsync(_world.Check(Doc("d1"), _view, _world.User("u1")));

        result.Decision.ShouldBe(CheckDecision.Conditional);
        result.Allowed.ShouldBeFalse();
        result.UnmetConditions.Count.ShouldBe(1);
        result.UnmetConditions[0].Condition.ShouldBe(_active);
        result.UnmetConditions[0].MissingKeys.ShouldBe([_flag]);
    }

    [Fact]
    public async Task Condition_false_with_complete_context_is_a_plain_deny()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Permission(_view, p => p.Relation(_viewer).Conditioned(_active)))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema, [TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1"))],
            [(Doc("d1"), new Dictionary<string, object?> { [_flag] = false })]);

        var result = await auth.CheckAsync(_world.Check(Doc("d1"), _view, _world.User("u1")));

        result.Decision.ShouldBe(CheckDecision.Deny);
        result.UnmetConditions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Structural_miss_for_a_different_subject_is_deny_not_conditional()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Permission(_view, p => p.Relation(_viewer).Conditioned(_active)))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema, [TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1"))]);

        var result = await auth.CheckAsync(_world.Check(Doc("d1"), _view, _world.User("u2")));

        result.Decision.ShouldBe(CheckDecision.Deny);
    }

    [Fact]
    public async Task Unconditioned_union_branch_absorbs_a_conditional_sibling_into_allow()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Relation(_other, s => s.User())
                .Permission(_view, p => p.Relation(_other).Union(u => u.Relation(_viewer).Conditioned(_active))))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema,
        [
            TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1")),
            TestWorld.Tuple(_doc, "d1", _other, _world.User("u1")),
        ]);

        var result = await auth.CheckAsync(_world.Check(Doc("d1"), _view, _world.User("u1")));

        result.Decision.ShouldBe(CheckDecision.Allow);
        result.UnmetConditions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sole_conditional_union_branch_is_conditional()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Relation(_other, s => s.User())
                .Permission(_view, p => p.Relation(_other).Union(u => u.Relation(_viewer).Conditioned(_active))))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema, [TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1"))]);

        var result = await auth.CheckAsync(_world.Check(Doc("d1"), _view, _world.User("u1")));

        result.Decision.ShouldBe(CheckDecision.Conditional);
        result.UnmetConditions[0].Condition.ShouldBe(_active);
    }

    [Fact]
    public async Task Intersection_with_a_definite_false_branch_absorbs_the_unknown_into_deny()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Relation(_other, s => s.User())
                .Permission(_view, p => p.Relation(_other).Intersect(x => x.Relation(_viewer).Conditioned(_active))))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema, [TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1"))]);

        var result = await auth.CheckAsync(_world.Check(Doc("d1"), _view, _world.User("u1")));

        result.Decision.ShouldBe(CheckDecision.Deny);
    }

    [Fact]
    public async Task Exclusion_with_a_missing_context_subtrahend_is_conditional_fail_closed()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Relation(_blocked, s => s.User())
                .Permission(_view, p => p.Relation(_viewer).Exclude(x => x.Relation(_blocked).Conditioned(_active))))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema,
        [
            TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1")),
            TestWorld.Tuple(_doc, "d1", _blocked, _world.User("u1")),
        ]);

        var result = await auth.CheckAsync(_world.Check(Doc("d1"), _view, _world.User("u1")));

        result.Decision.ShouldBe(CheckDecision.Conditional);
        result.Allowed.ShouldBeFalse();
        result.UnmetConditions[0].Condition.ShouldBe(_active);
    }

    [Fact]
    public async Task Batch_reports_independent_per_item_decisions_over_a_shared_context()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Permission(_view, p => p.Relation(_viewer).Conditioned(_active)))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema,
        [
            TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1")),
            TestWorld.Tuple(_doc, "d2", _viewer, _world.User("u1")),
        ],
        [(Doc("d2"), new Dictionary<string, object?> { [_flag] = true })]);

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, _world.User("u1"),
            new Dictionary<string, object?>());
        var items = new[]
        {
            new CheckItem(Doc("d1"), _view, _world.User("u1")),
            new CheckItem(Doc("d2"), _view, _world.User("u1")),
            new CheckItem(Doc("d1"), _view, _world.User("u2")),
        };

        var results = await auth.BatchCheckAsync(new BatchCheckRequest(_world.Tenant, items, ctx));

        results[0].Decision.ShouldBe(CheckDecision.Conditional);
        results[0].UnmetConditions[0].MissingKeys.ShouldBe([_flag]);
        results[1].Decision.ShouldBe(CheckDecision.Allow);
        results[2].Decision.ShouldBe(CheckDecision.Deny);
    }

    [Fact]
    public async Task Memo_hit_reproduces_the_conditional_decision_and_unmet_set()
    {
        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(_doc, t => t
                .Relation(_viewer, s => s.User())
                .Permission(_view, p => p.Relation(_viewer).Conditioned(_active)))
            .Condition(_active, _ => { }, x => x.Eq(x.Attribute(_flag), x.Const(true)))
            .Build();

        var auth = await BuildAsync(schema, [TestWorld.Tuple(_doc, "d1", _viewer, _world.User("u1"))]);

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, _world.User("u1"),
            new Dictionary<string, object?>());
        var item = new CheckItem(Doc("d1"), _view, _world.User("u1"));

        var results = await auth.BatchCheckAsync(new BatchCheckRequest(_world.Tenant, [item, item], ctx));

        results[0].ShouldBe(results[1]);
        results[0].Decision.ShouldBe(CheckDecision.Conditional);
        results[1].UnmetConditions[0].Condition.ShouldBe(_active);
    }
}
