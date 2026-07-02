using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class ConditionMissingContextTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoAttrs = new Dictionary<string, object?>();
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private readonly TestWorld _world = TestWorld.New();

    private RequestContext Context(IReadOnlyDictionary<string, object?>? attrs = null) =>
        new(DateTimeOffset.UnixEpoch, _world.User(_world.SubjectId()), attrs ?? NoAttrs);

    private ConditionDef Gate(string attr) =>
        new(_world.ConditionName(), [], new Compare(new AttributeRef(attr), CompareOp.Eq, new LiteralBool(true)));

    [Fact]
    public void Absent_attribute_is_missing_context_naming_the_key()
    {
        var attr = _world.ParamName();

        var r = ConditionEvaluator.Evaluate(Gate(attr), NoAttrs, Context(), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.MissingContext);
        r.Allowed.ShouldBeFalse();
        r.MissingKeys.ShouldBe([attr]);
        r.Diagnostic.ShouldNotBeNull();
        r.Diagnostic!.ShouldContain(attr);
    }

    [Fact]
    public void Attribute_supplied_through_request_context_resolves_definitely()
    {
        var attr = _world.ParamName();

        var r = ConditionEvaluator.Evaluate(Gate(attr), NoAttrs,
            Context(new Dictionary<string, object?> { [attr] = true }), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.Satisfied);
        r.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Resource_attribute_is_present_so_not_missing_context()
    {
        var attr = _world.ParamName();

        var r = ConditionEvaluator.Evaluate(Gate(attr),
            new Dictionary<string, object?> { [attr] = true }, Context(), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.Satisfied);
    }

    [Fact]
    public void Resource_attribute_wins_over_a_conflicting_request_context_value()
    {
        var attr = _world.ParamName();

        var resourceTrue = ConditionEvaluator.Evaluate(Gate(attr),
            new Dictionary<string, object?> { [attr] = true },
            Context(new Dictionary<string, object?> { [attr] = false }), NoParams);

        var resourceFalse = ConditionEvaluator.Evaluate(Gate(attr),
            new Dictionary<string, object?> { [attr] = false },
            Context(new Dictionary<string, object?> { [attr] = true }), NoParams);

        resourceTrue.Resolution.ShouldBe(ConditionResolution.Satisfied);
        resourceFalse.Resolution.ShouldBe(ConditionResolution.Unsatisfied);
    }

    [Fact]
    public void Null_resource_attribute_defers_to_the_request_context()
    {
        var attr = _world.ParamName();

        var r = ConditionEvaluator.Evaluate(Gate(attr),
            new Dictionary<string, object?> { [attr] = null },
            Context(new Dictionary<string, object?> { [attr] = true }), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.Satisfied);
    }

    [Fact]
    public void Absorbing_disjunction_ignores_a_missing_operand()
    {
        var attr = _world.ParamName();
        var body = new BoolOp(
            new Compare(new AttributeRef(attr), CompareOp.Eq, new LiteralBool(true)),
            BoolConnective.Or, new LiteralBool(true));
        var def = new ConditionDef(_world.ConditionName(), [], body);

        var r = ConditionEvaluator.Evaluate(def, NoAttrs, Context(), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.Satisfied);
    }

    [Fact]
    public void Absorbing_conjunction_ignores_a_missing_operand()
    {
        var attr = _world.ParamName();
        var body = new BoolOp(
            new Compare(new AttributeRef(attr), CompareOp.Eq, new LiteralBool(true)),
            BoolConnective.And, new LiteralBool(false));
        var def = new ConditionDef(_world.ConditionName(), [], body);

        var r = ConditionEvaluator.Evaluate(def, NoAttrs, Context(), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.Unsatisfied);
        r.Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Two_live_missing_attributes_report_both_keys_deduped_and_ordered()
    {
        var a1 = _world.ParamName();
        var a2 = _world.ParamName();
        var body = new BoolOp(
            new Compare(new AttributeRef(a1), CompareOp.Eq, new LiteralBool(true)),
            BoolConnective.And,
            new Compare(new AttributeRef(a2), CompareOp.Eq, new LiteralBool(true)));
        var def = new ConditionDef(_world.ConditionName(), [], body);

        var r = ConditionEvaluator.Evaluate(def, NoAttrs, Context(), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.MissingContext);
        r.MissingKeys.ShouldBe(new[] { a1, a2 }.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Missing_parameter_is_default_deny_not_missing_context()
    {
        var p = _world.ParamName();
        var def = new ConditionDef(_world.ConditionName(),
            [new ConditionParam(p, ConditionType.Int)],
            new Compare(new ParamRef(p), CompareOp.Ge, new LiteralInt(1)));

        var r = ConditionEvaluator.Evaluate(def, NoAttrs, Context(), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.Unsatisfied);
        r.Allowed.ShouldBeFalse();
        r.Diagnostic.ShouldNotBeNull();
    }

    [Fact]
    public void Present_but_wrong_type_is_default_deny_not_missing_context()
    {
        var attr = _world.ParamName();
        var def = new ConditionDef(_world.ConditionName(), [],
            new Compare(new AttributeRef(attr), CompareOp.Ge, new LiteralInt(1)));

        var r = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { [attr] = "not-a-number" }, Context(), NoParams);

        r.Resolution.ShouldBe(ConditionResolution.Unsatisfied);
        r.Diagnostic.ShouldNotBeNull();
    }
}
