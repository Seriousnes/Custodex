using System.Text.Json;

using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class ConditionEvaluatorJsonElementTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _field;

    public ConditionEvaluatorJsonElementTests()
    {
        _field = _world.ParamName();
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ConditionResult EvaluateWithAttribute(ConditionExpr body, object? attributeValue)
    {
        var def = new ConditionDef(_world.ConditionName(), [], body);
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal) { [_field] = attributeValue };
        var ctx = new RequestContext(
            DateTimeOffset.UnixEpoch, _world.User(_world.SubjectId()),
            new Dictionary<string, object?>(StringComparer.Ordinal));
        return ConditionEvaluator.Evaluate(def, attributes, ctx, new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    [Fact]
    public void Bool_json_element_resolves_to_its_boolean()
    {
        var body = new Compare(new AttributeRef(_field), CompareOp.Eq, new LiteralBool(true));

        EvaluateWithAttribute(body, Element("true")).Allowed.ShouldBeTrue();
        EvaluateWithAttribute(body, Element("false")).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Integer_json_element_resolves_as_a_number()
    {
        var body = new Compare(new AttributeRef(_field), CompareOp.Ge, new LiteralInt(5));

        EvaluateWithAttribute(body, Element("10")).Allowed.ShouldBeTrue();
        EvaluateWithAttribute(body, Element("1")).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Real_json_element_resolves_as_a_number()
    {
        var body = new Compare(new AttributeRef(_field), CompareOp.Lt, new LiteralDouble(2.5));

        EvaluateWithAttribute(body, Element("1.25")).Allowed.ShouldBeTrue();
        EvaluateWithAttribute(body, Element("9.75")).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void String_json_element_resolves_to_its_text()
    {
        var match = _world.SubjectId();
        var body = new Compare(new AttributeRef(_field), CompareOp.Eq, new LiteralString(match));

        EvaluateWithAttribute(body, Element(JsonSerializer.Serialize(match))).Allowed.ShouldBeTrue();
        EvaluateWithAttribute(body, Element(JsonSerializer.Serialize(_world.SubjectId()))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Null_object_and_array_json_elements_never_satisfy_a_condition()
    {
        var body = new Compare(new AttributeRef(_field), CompareOp.Eq, new LiteralBool(true));

        EvaluateWithAttribute(body, Element("null")).Allowed.ShouldBeFalse();
        EvaluateWithAttribute(body, Element("{}")).Allowed.ShouldBeFalse();
        EvaluateWithAttribute(body, Element("[]")).Allowed.ShouldBeFalse();
    }
}
