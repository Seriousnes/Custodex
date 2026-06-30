using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Dsl;
using Custodex.Core.Dsl.Parsing;

using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class TimestampAttrDslTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();

    private static ConditionDef ParseCond(string src) =>
        SchemaParser.Parse($"condition {src}").Conditions.Single();

    private static Schema CondSchema(ConditionExpr body) =>
        new("v1", [], [new ConditionDef("fresh", [], body)]);

    [Fact]
    public void Typed_timestamp_attribute_survives_write_then_parse()
    {
        var body = new Compare(new AttributeRef("expires", ConditionType.Timestamp), CompareOp.Le, new ContextNow());
        var schema = CondSchema(body);

        var reparsed = SchemaParser.Parse(SchemaWriter.Write(schema));

        reparsed.Conditions.Single().Body.ShouldBe(body);
    }

    [Fact]
    public void Typed_timestamp_attribute_vs_attribute_comparison_round_trips()
    {
        var body = new Compare(
            new AttributeRef("expires", ConditionType.Timestamp),
            CompareOp.Le,
            new AttributeRef("renewed", ConditionType.Timestamp));
        var schema = CondSchema(body);

        var reparsed = SchemaParser.Parse(SchemaWriter.Write(schema));

        reparsed.Conditions.Single().Body.ShouldBe(body);
    }

    [Fact]
    public void Typed_timestamp_attribute_write_is_text_idempotent()
    {
        var body = new Compare(new AttributeRef("expires", ConditionType.Timestamp), CompareOp.Le, new ContextNow());
        var schema = CondSchema(body);

        var text = SchemaWriter.Write(schema);

        SchemaWriter.Write(SchemaParser.Parse(text)).ShouldBe(text);
    }

    [Fact]
    public void Typed_timestamp_attribute_parses_with_timestamp_type()
    {
        var cond = ParseCond("at_least() = timestamp(resource[\"expires\"]) <= context.now");

        var compare = cond.Body.ShouldBeOfType<Compare>();
        var attr = compare.Left.ShouldBeOfType<AttributeRef>();
        attr.Field.ShouldBe("expires");
        attr.Type.ShouldBe(ConditionType.Timestamp);
    }

    [Fact]
    public void Typed_timestamp_attribute_writes_with_cast_syntax()
    {
        var body = new Compare(new AttributeRef("expires", ConditionType.Timestamp), CompareOp.Le, new ContextNow());

        var text = SchemaWriter.Write(CondSchema(body));

        text.ShouldContain("timestamp(resource[\"expires\"])");
    }

    [Fact]
    public void Untyped_attribute_round_trips_byte_identically()
    {
        var body = new Compare(new AttributeRef("weight"), CompareOp.Ge, new ParamRef("n"));
        var schema = new Schema("v1", [], [new ConditionDef("at_least", [new ConditionParam("n", ConditionType.Int)], body)]);

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("resource[\"weight\"]");
        text.ShouldNotContain("timestamp(");
        SchemaWriter.Write(SchemaParser.Parse(text)).ShouldBe(text);
        SchemaParser.Parse(text).Conditions.Single().Body.ShouldBe(body);
    }

    [Fact]
    public void Cast_of_a_non_attribute_expression_is_a_parse_error()
    {
        Should.Throw<DslParseException>(() => SchemaParser.Parse("condition bad() = timestamp(context.now) == context.now"));
    }

    [Fact]
    public void Dsl_typed_timestamp_attribute_compares_chronologically_when_evaluated()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var cond = ParseCond("fresh() = context.now <= timestamp(resource[\"expires\"])");
        var ctx = new RequestContext(now, new SubjectRef("user", "a"), new Dictionary<string, object?>());

        ConditionEvaluator.Evaluate(cond,
            new Dictionary<string, object?> { ["expires"] = now.AddHours(1).ToString("O") },
            ctx, NoParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(cond,
            new Dictionary<string, object?> { ["expires"] = now.AddHours(-1).ToString("O") },
            ctx, NoParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Dsl_typed_timestamp_attributes_order_chronologically_not_ordinally()
    {
        var cond = ParseCond("ordered() = timestamp(resource[\"expires\"]) <= timestamp(resource[\"renewed\"])");
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "a"), new Dictionary<string, object?>());

        ConditionEvaluator.Evaluate(cond,
            new Dictionary<string, object?>
            {
                ["expires"] = "2024-01-01T00:00:00+00:00",
                ["renewed"] = "2024-01-01T05:00:00+00:00",
            },
            ctx, NoParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(cond,
            new Dictionary<string, object?>
            {
                ["expires"] = "2024-01-01T05:00:00+00:00",
                ["renewed"] = "2024-01-01T00:00:00+00:00",
            },
            ctx, NoParams).Allowed.ShouldBeFalse();
    }
}
