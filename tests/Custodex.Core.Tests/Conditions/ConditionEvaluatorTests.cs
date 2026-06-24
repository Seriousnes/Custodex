using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class ConditionEvaluatorTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoAttrs = new Dictionary<string, object?>();

    private readonly TestWorld _world = TestWorld.New();
    private readonly string _withinHours;
    private readonly string _startParam;
    private readonly string _endParam;
    private readonly string _atLeast;
    private readonly string _nParam;
    private readonly string _measure;
    private readonly string _isCreator;
    private readonly string _creatorAttr;

    public ConditionEvaluatorTests()
    {
        _withinHours = _world.ConditionName();
        _startParam = _world.ParamName();
        _endParam = _world.ParamName();
        _atLeast = _world.ConditionName();
        _nParam = _world.ParamName();
        _measure = _world.ParamName();
        _isCreator = _world.ConditionName();
        _creatorAttr = _world.ParamName();
    }

    private RequestContext Context(DateTimeOffset now, string subjectId) =>
        new(now, _world.User(subjectId), new Dictionary<string, object?>());

    private ConditionDef WithinHours() => new(
        _withinHours,
        [new ConditionParam(_startParam, ConditionType.Int), new ConditionParam(_endParam, ConditionType.Int)],
        new BoolOp(
            new Compare(new HourOf(new ContextNow()), CompareOp.Ge, new ParamRef(_startParam)),
            BoolConnective.And,
            new Compare(new HourOf(new ContextNow()), CompareOp.Lt, new ParamRef(_endParam))));

    private ConditionDef AtLeastMeasure() => new(
        _atLeast,
        [new ConditionParam(_nParam, ConditionType.Int)],
        new Compare(new AttributeRef(_measure), CompareOp.Ge, new ParamRef(_nParam)));

    private ConditionDef IsCreator() => new(
        _isCreator,
        [],
        new Compare(new AttributeRef(_creatorAttr), CompareOp.Eq, new ContextSubject()));

    [Fact]
    public void Within_hours_allows_inside_window_and_denies_outside()
    {
        var def = WithinHours();
        var p = new Dictionary<string, object?> { [_startParam] = 8, [_endParam] = 18 };
        var subject = _world.SubjectId();

        ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero), subject), p)
            .Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(new DateTimeOffset(2026, 6, 23, 20, 0, 0, TimeSpan.Zero), subject), p)
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public void At_least_compares_attribute_to_parameter()
    {
        var def = AtLeastMeasure();
        var ctx = Context(DateTimeOffset.UnixEpoch, _world.SubjectId());

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_measure] = 50 }, ctx,
            new Dictionary<string, object?> { [_nParam] = 30 }).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_measure] = 10 }, ctx,
            new Dictionary<string, object?> { [_nParam] = 30 }).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Is_creator_compares_attribute_to_subject_id()
    {
        var def = IsCreator();
        var noParams = new Dictionary<string, object?>();
        var creator = _world.SubjectId();
        var other = _world.SubjectId();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_creatorAttr] = creator },
            Context(DateTimeOffset.UnixEpoch, creator), noParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_creatorAttr] = other },
            Context(DateTimeOffset.UnixEpoch, creator), noParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Missing_attribute_is_a_deny_with_a_diagnostic_not_an_exception()
    {
        var def = AtLeastMeasure();
        var result = ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(DateTimeOffset.UnixEpoch, _world.SubjectId()),
            new Dictionary<string, object?> { [_nParam] = 30 });

        result.Allowed.ShouldBeFalse();
        result.Diagnostic!.ShouldContain(_measure);
    }

    [Fact]
    public void Type_mismatch_is_a_deny_with_a_diagnostic()
    {
        var def = AtLeastMeasure();
        var result = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_measure] = "not-a-number" },
            Context(DateTimeOffset.UnixEpoch, _world.SubjectId()),
            new Dictionary<string, object?> { [_nParam] = 30 });

        result.Allowed.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull();
    }
}
