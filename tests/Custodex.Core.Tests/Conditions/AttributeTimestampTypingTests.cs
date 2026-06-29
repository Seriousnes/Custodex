using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class AttributeTimestampTypingTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private readonly TestWorld _world = TestWorld.New();
    private readonly string _condition;
    private readonly string _left;
    private readonly string _right;
    private readonly string _untilParam;

    public AttributeTimestampTypingTests()
    {
        _condition = _world.ConditionName();
        _left = _world.ParamName();
        _right = _world.ParamName();
        _untilParam = _world.ParamName();
    }

    private RequestContext Context(DateTimeOffset now) =>
        new(now, _world.User(_world.SubjectId()), new Dictionary<string, object?>());

    private ConditionResult Run(ConditionExpr body, IReadOnlyDictionary<string, object?> attributes, DateTimeOffset now) =>
        ConditionEvaluator.Evaluate(new ConditionDef(_condition, [], body), attributes, Context(now), NoParams);

    private ConditionDef Compare2(ConditionExpr left, CompareOp op, ConditionExpr right) =>
        new(_condition, [], new Compare(left, op, right));

    private static AttributeRef Ts(string field) => new(field, ConditionType.Timestamp);

    [Fact]
    public void Declared_timestamp_attribute_arriving_as_string_coerces_against_now()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var def = Compare2(new ContextNow(), CompareOp.Le, Ts(_left));

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_left] = now.AddHours(1).ToString("O") },
            Context(now), NoParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_left] = now.AddHours(-1).ToString("O") },
            Context(now), NoParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Two_declared_timestamp_attributes_same_instant_different_encoding_compare_equal()
    {
        var def = Compare2(Ts(_left), CompareOp.Eq, Ts(_right));
        var attrs = new Dictionary<string, object?>
        {
            [_left] = "2024-01-01T00:00:00+00:00",
            [_right] = "2024-01-01T01:00:00+01:00",
        };

        Run(def.Body, attrs, DateTimeOffset.UnixEpoch).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Two_declared_timestamp_attributes_order_chronologically_not_ordinally()
    {
        var earlier = "2024-01-01T00:00:00+00:00";
        var later = "2024-01-01T05:00:00+00:00";

        Run(new Compare(Ts(_left), CompareOp.Le, Ts(_right)),
            new Dictionary<string, object?> { [_left] = earlier, [_right] = later },
            DateTimeOffset.UnixEpoch).Allowed.ShouldBeTrue();

        Run(new Compare(Ts(_left), CompareOp.Le, Ts(_right)),
            new Dictionary<string, object?> { [_left] = later, [_right] = earlier },
            DateTimeOffset.UnixEpoch).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Declared_timestamp_attribute_accepts_a_raw_datetimeoffset()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Run(new Compare(Ts(_left), CompareOp.Ge, new ContextNow()),
            new Dictionary<string, object?> { [_left] = now.AddHours(1) },
            now).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Declared_timestamp_attribute_against_a_timestamp_param_still_works()
    {
        var anchor = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var def = new ConditionDef(_condition,
            [new ConditionParam(_untilParam, ConditionType.Timestamp)],
            new Compare(Ts(_left), CompareOp.Le, new ParamRef(_untilParam)));

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_left] = anchor.ToString("O") },
            Context(anchor),
            new Dictionary<string, object?> { [_untilParam] = anchor.AddHours(1).ToString("O") }).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [_left] = anchor.AddHours(2).ToString("O") },
            Context(anchor),
            new Dictionary<string, object?> { [_untilParam] = anchor.AddHours(1).ToString("O") }).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Malformed_declared_timestamp_attribute_is_a_deny_with_a_diagnostic_not_an_exception()
    {
        var result = Run(new Compare(Ts(_left), CompareOp.Eq, Ts(_right)),
            new Dictionary<string, object?>
            {
                [_left] = "not-a-timestamp",
                [_right] = "2024-01-01T00:00:00+00:00",
            },
            DateTimeOffset.UnixEpoch);

        result.Allowed.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull();
    }

    [Fact]
    public void Undeclared_attributes_same_instant_different_encoding_stay_ordinal_and_compare_unequal()
    {
        var def = Compare2(new AttributeRef(_left), CompareOp.Eq, new AttributeRef(_right));
        var attrs = new Dictionary<string, object?>
        {
            [_left] = "2024-01-01T00:00:00+00:00",
            [_right] = "2024-01-01T01:00:00+01:00",
        };

        Run(def.Body, attrs, DateTimeOffset.UnixEpoch).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Undeclared_attributes_with_identical_date_shaped_strings_stay_ordinal_and_compare_equal()
    {
        var def = Compare2(new AttributeRef(_left), CompareOp.Eq, new AttributeRef(_right));

        Run(def.Body,
            new Dictionary<string, object?> { [_left] = "2024-01-01", [_right] = "2024-01-01" },
            DateTimeOffset.UnixEpoch).Allowed.ShouldBeTrue();

        Run(def.Body,
            new Dictionary<string, object?> { [_left] = "2024", [_right] = "2025" },
            DateTimeOffset.UnixEpoch).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Declared_string_attribute_does_not_trigger_timestamp_coercion()
    {
        var def = Compare2(
            new AttributeRef(_left, ConditionType.String),
            CompareOp.Eq,
            new AttributeRef(_right, ConditionType.String));
        var attrs = new Dictionary<string, object?>
        {
            [_left] = "2024-01-01T00:00:00+00:00",
            [_right] = "2024-01-01T01:00:00+01:00",
        };

        Run(def.Body, attrs, DateTimeOffset.UnixEpoch).Allowed.ShouldBeFalse();
    }
}
