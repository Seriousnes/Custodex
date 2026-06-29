using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class ConditionEvaluatorTimestampTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoAttrs = new Dictionary<string, object?>();
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private readonly TestWorld _world = TestWorld.New();
    private readonly string _condition;
    private readonly string _expiresAttr;
    private readonly string _untilParam;

    public ConditionEvaluatorTimestampTests()
    {
        _condition = _world.ConditionName();
        _expiresAttr = _world.ParamName();
        _untilParam = _world.ParamName();
    }

    private RequestContext Context(DateTimeOffset now) =>
        new(now, _world.User(_world.SubjectId()), new Dictionary<string, object?>());

    private static string Iso(DateTimeOffset value) => value.ToString("O");

    private ConditionDef NotExpired() => new(
        _condition, [],
        new Compare(new ContextNow(), CompareOp.Le, new AttributeRef(_expiresAttr)));

    private ConditionDef BeforeUntil() => new(
        _condition,
        [new ConditionParam(_untilParam, ConditionType.Timestamp)],
        new Compare(new ContextNow(), CompareOp.Le, new ParamRef(_untilParam)));

    [Fact]
    public void Attribute_string_is_coerced_when_compared_against_now()
    {
        var def = NotExpired();
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = Iso(now.AddHours(1)) },
            Context(now), NoParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = Iso(now.AddHours(-1)) },
            Context(now), NoParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Attribute_string_is_coerced_when_it_is_the_left_operand()
    {
        var def = new ConditionDef(_condition, [],
            new Compare(new AttributeRef(_expiresAttr), CompareOp.Ge, new ContextNow()));
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = Iso(now.AddHours(1)) },
            Context(now), NoParams).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Param_declared_timestamp_arriving_as_string_is_coerced()
    {
        var def = BeforeUntil();
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ConditionEvaluator.Evaluate(def, NoAttrs, Context(now),
            new Dictionary<string, object?> { [_untilParam] = Iso(now.AddHours(1)) }).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def, NoAttrs, Context(now),
            new Dictionary<string, object?> { [_untilParam] = Iso(now.AddHours(-1)) }).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Param_declared_timestamp_still_accepts_a_raw_datetimeoffset()
    {
        var def = BeforeUntil();
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ConditionEvaluator.Evaluate(def, NoAttrs, Context(now),
            new Dictionary<string, object?> { [_untilParam] = now.AddHours(1) }).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Attribute_string_is_coerced_against_a_string_typed_timestamp_param()
    {
        var def = new ConditionDef(_condition,
            [new ConditionParam(_untilParam, ConditionType.Timestamp)],
            new Compare(new AttributeRef(_expiresAttr), CompareOp.Le, new ParamRef(_untilParam)));
        var anchor = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = Iso(anchor) },
            Context(anchor),
            new Dictionary<string, object?> { [_untilParam] = Iso(anchor.AddHours(1)) }).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = Iso(anchor.AddHours(2)) },
            Context(anchor),
            new Dictionary<string, object?> { [_untilParam] = Iso(anchor.AddHours(1)) }).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Same_instant_in_different_offsets_compares_equal_as_timestamps()
    {
        var def = new ConditionDef(_condition,
            [new ConditionParam(_untilParam, ConditionType.Timestamp)],
            new Compare(new AttributeRef(_expiresAttr), CompareOp.Eq, new ParamRef(_untilParam)));

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "2024-01-01T00:00:00+00:00" },
            Context(DateTimeOffset.UnixEpoch),
            new Dictionary<string, object?> { [_untilParam] = "2024-01-01T01:00:00+01:00" }).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Malformed_timestamp_param_is_a_deny_with_a_diagnostic_not_an_exception()
    {
        var def = BeforeUntil();
        var result = ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(DateTimeOffset.UnixEpoch),
            new Dictionary<string, object?> { [_untilParam] = "not-a-timestamp" });

        result.Allowed.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull();
    }

    [Fact]
    public void Malformed_timestamp_attribute_against_now_is_a_deny_with_a_diagnostic()
    {
        var def = NotExpired();
        var result = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "not-a-timestamp" },
            Context(DateTimeOffset.UnixEpoch), NoParams);

        result.Allowed.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull();
    }

    [Fact]
    public void Hour_of_a_string_attribute_is_coerced()
    {
        var def = new ConditionDef(_condition, [],
            new Compare(new HourOf(new AttributeRef(_expiresAttr)), CompareOp.Eq, new LiteralInt(9)));

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "2026-01-01T09:30:00+00:00" },
            Context(DateTimeOffset.UnixEpoch), NoParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "2026-01-01T10:30:00+00:00" },
            Context(DateTimeOffset.UnixEpoch), NoParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void String_vs_string_stays_ordinal_for_a_plain_value()
    {
        var def = new ConditionDef(_condition, [],
            new Compare(new AttributeRef(_expiresAttr), CompareOp.Eq, new LiteralString("2024")));

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "2024" },
            Context(DateTimeOffset.UnixEpoch), NoParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "2025" },
            Context(DateTimeOffset.UnixEpoch), NoParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void String_vs_string_stays_ordinal_for_date_shaped_same_instant_values()
    {
        var def = new ConditionDef(_condition, [],
            new Compare(
                new AttributeRef(_expiresAttr),
                CompareOp.Eq,
                new LiteralString("2024-01-01T00:00:00+00:00")));

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "2024-01-01T01:00:00+01:00" },
            Context(DateTimeOffset.UnixEpoch), NoParams).Allowed.ShouldBeFalse();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_expiresAttr] = "2024-01-01T00:00:00+00:00" },
            Context(DateTimeOffset.UnixEpoch), NoParams).Allowed.ShouldBeTrue();
    }
}
