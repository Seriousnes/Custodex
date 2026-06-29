using System.Text.Json.Nodes;

using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Serialization;

using Shouldly;

namespace Custodex.Core.Tests.Serialization;

public class AttributeRefTypeJsonTests
{
    private static Schema SchemaWithBody(ConditionExpr body) => new("v1",
        [new EntityTypeDef("doc", [], [])],
        [new ConditionDef("fresh", [], body)]);

    [Fact]
    public void Typed_attribute_ref_round_trips_inside_a_schema()
    {
        var schema = SchemaWithBody(new AttributeRef("expires", ConditionType.Timestamp));

        var back = SchemaJson.Deserialize(SchemaJson.Serialize(schema));

        back!.Conditions.Single().Body.ShouldBeOfType<AttributeRef>().Type.ShouldBe(ConditionType.Timestamp);
    }

    [Fact]
    public void Untyped_attribute_ref_omits_the_type_property_and_round_trips_to_null()
    {
        var schema = SchemaWithBody(new AttributeRef("expires"));
        var json = SchemaJson.Serialize(schema);

        var body = JsonNode.Parse(json)!["conditions"]![0]!["body"]!.AsObject();
        body["$kind"]!.GetValue<string>().ShouldBe("attributeRef");
        body.ContainsKey("type").ShouldBeFalse();

        SchemaJson.Deserialize(json)!.Conditions.Single().Body
            .ShouldBeOfType<AttributeRef>().Type.ShouldBeNull();
    }

    [Fact]
    public void Legacy_attribute_ref_without_a_type_property_deserializes_to_null()
    {
        const string legacy =
            """{"version":"v1","types":[],"conditions":[{"name":"fresh","parameters":[],"body":{"$kind":"attributeRef","field":"expires"}}]}""";

        var back = SchemaJson.Deserialize(legacy)!.Conditions.Single().Body.ShouldBeOfType<AttributeRef>();
        back.Field.ShouldBe("expires");
        back.Type.ShouldBeNull();
    }
}
