using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Conditions;

public class ConditionEvaluatorTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoAttrs = new Dictionary<string, object?>();

    private static RequestContext Context(DateTimeOffset now, string subjectId) =>
        new(now, new SubjectRef("user", subjectId), new Dictionary<string, object?>());

    // within_hours(start, end) = context.now.hour >= start && context.now.hour < end
    private static ConditionDef WithinHours() => new(
        "within_hours",
        [new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int)],
        new BoolOp(
            new Compare(new HourOf(new ContextNow()), CompareOp.Ge, new ParamRef("start")),
            BoolConnective.And,
            new Compare(new HourOf(new ContextNow()), CompareOp.Lt, new ParamRef("end"))));

    // at_least(field, n) = resource[field] >= n  — 'field' selects the attribute name.
    private static ConditionDef AtLeastWeight() => new(
        "at_least",
        [new ConditionParam("n", ConditionType.Int)],
        new Compare(new AttributeRef("weight"), CompareOp.Ge, new ParamRef("n")));

    // is_creator() = resource.created_by == context.subject
    private static ConditionDef IsCreator() => new(
        "is_creator",
        [],
        new Compare(new AttributeRef("created_by"), CompareOp.Eq, new ContextSubject()));

    [Fact]
    public void Within_hours_allows_inside_window_and_denies_outside()
    {
        var def = WithinHours();
        var p = new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 };

        ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero), "dr-smith"), p)
            .Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(new DateTimeOffset(2026, 6, 23, 20, 0, 0, TimeSpan.Zero), "dr-smith"), p)
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public void At_least_compares_attribute_to_parameter()
    {
        var def = AtLeastWeight();
        var ctx = Context(DateTimeOffset.UnixEpoch, "dr-smith");

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = 50 }, ctx,
            new Dictionary<string, object?> { ["n"] = 30 }).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = 10 }, ctx,
            new Dictionary<string, object?> { ["n"] = 30 }).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Is_creator_compares_created_by_to_subject_id()
    {
        var def = IsCreator();
        var noParams = new Dictionary<string, object?>();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["created_by"] = "dr-smith" },
            Context(DateTimeOffset.UnixEpoch, "dr-smith"), noParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["created_by"] = "alice" },
            Context(DateTimeOffset.UnixEpoch, "dr-smith"), noParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Missing_attribute_is_a_deny_with_a_diagnostic_not_an_exception()
    {
        var def = AtLeastWeight();
        var result = ConditionEvaluator.Evaluate(def, NoAttrs,   // no 'weight'
            Context(DateTimeOffset.UnixEpoch, "dr-smith"),
            new Dictionary<string, object?> { ["n"] = 30 });

        result.Allowed.ShouldBeFalse();
        result.Diagnostic!.ShouldContain("weight");
    }

    [Fact]
    public void Type_mismatch_is_a_deny_with_a_diagnostic()
    {
        var def = AtLeastWeight();
        var result = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = "heavy" },   // string vs int compare
            Context(DateTimeOffset.UnixEpoch, "dr-smith"),
            new Dictionary<string, object?> { ["n"] = 30 });

        result.Allowed.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull();
    }
}
