using System.Text.Json.Nodes;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

public class AttributeRefTypeJsonTests
{
    [Fact]
    public void Typed_attribute_ref_round_trips_inside_a_full_schema()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("editor", s => s.Type("user"))
                .Permission("edit", p => p.Relation("editor")))
            .Condition("fresh", _ => { }, b => b.Le(b.Attribute("expires", ConditionType.Timestamp), b.Now()))
            .Build();

        var back = Json.Deserialize<Schema>(Json.Serialize(schema));

        var body = back!.Conditions.Single().Body.ShouldBeOfType<Compare>();
        body.Left.ShouldBeOfType<AttributeRef>().Type.ShouldBe(ConditionType.Timestamp);
    }

    [Fact]
    public void Typed_attribute_ref_round_trips_as_a_standalone_node()
    {
        ConditionExpr typed = new AttributeRef("expires", ConditionType.Timestamp);

        var back = Json.Deserialize<ConditionExpr>(Json.Serialize(typed));

        back.ShouldBeOfType<AttributeRef>().Type.ShouldBe(ConditionType.Timestamp);
    }

    [Fact]
    public void Untyped_attribute_ref_omits_the_type_property_and_round_trips_to_null()
    {
        ConditionExpr untyped = new AttributeRef("expires");
        var json = Json.Serialize(untyped);

        JsonNode.Parse(json)!.AsObject().ContainsKey("type").ShouldBeFalse();
        Json.Deserialize<ConditionExpr>(json).ShouldBeOfType<AttributeRef>().Type.ShouldBeNull();
    }

    [Fact]
    public void Legacy_attribute_ref_without_a_type_property_deserializes_to_null()
    {
        const string legacy = """{"$type":"attributeRef","field":"expires"}""";

        var back = Json.Deserialize<ConditionExpr>(legacy).ShouldBeOfType<AttributeRef>();
        back.Field.ShouldBe("expires");
        back.Type.ShouldBeNull();
    }
}
