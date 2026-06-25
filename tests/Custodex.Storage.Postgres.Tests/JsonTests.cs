using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

public class JsonTests
{
    [Fact]
    public void Round_trips_a_dictionary()
    {
        var dict = new Dictionary<string, object?> { ["start"] = 8, ["label"] = "am" };
        var json = Json.Serialize(dict);
        var back = Json.Deserialize<Dictionary<string, object?>>(json);
        back.ShouldNotBeNull();
        back!["label"]!.ToString().ShouldBe("am");
    }

    [Fact]
    public void Deserialize_returns_default_for_null()
    {
        Json.Deserialize<Dictionary<string, object?>>(null).ShouldBeNull();
    }

    [Fact]
    public void Round_trips_a_polymorphic_perm_expression()
    {
        PermExpr expr = new Exclude(
            new Union(new RelationRef("editor"), new Arrow("container", "edit")),
            new RelationRef("blocked"));

        var json = Json.Serialize(expr);
        var back = Json.Deserialize<PermExpr>(json);

        var exclude = back.ShouldBeOfType<Exclude>();
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("blocked");
        var union = exclude.Left.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("editor");
        union.Right.ShouldBeOfType<Arrow>().Permission.ShouldBe("edit");
    }

    [Fact]
    public void Round_trips_a_full_schema_through_jsonb_text()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("resource",
                [new RelationDef("editor", [new SubjectTypeRef("user")])],
                [new PermissionDef("edit", new RelationRef("editor"))])],
            []);

        var back = Json.Deserialize<Schema>(Json.Serialize(schema));
        back!.Version.ShouldBe("v1");
        back.Types.Single().Permissions.Single().Expression.ShouldBeOfType<RelationRef>();
    }

    [Fact]
    public void Round_trips_a_polymorphic_condition_expression()
    {
        ConditionExpr body = new BoolOp(
            new Compare(new ParamRef("start"), CompareOp.Le, new HourOf(new ContextNow())),
            BoolConnective.And,
            new Not(new LiteralBool(false)));

        var back = Json.Deserialize<ConditionExpr>(Json.Serialize(body));

        var boolOp = back.ShouldBeOfType<BoolOp>();
        boolOp.Op.ShouldBe(BoolConnective.And);
        var compare = boolOp.Left.ShouldBeOfType<Compare>();
        compare.Op.ShouldBe(CompareOp.Le);
        compare.Left.ShouldBeOfType<ParamRef>().Name.ShouldBe("start");
        compare.Right.ShouldBeOfType<HourOf>().Timestamp.ShouldBeOfType<ContextNow>();
        boolOp.Right.ShouldBeOfType<Not>().Inner.ShouldBeOfType<LiteralBool>().Value.ShouldBeFalse();
    }

    [Fact]
    public void Round_trips_a_schema_with_a_non_empty_condition_body()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("resource",
                [new RelationDef("editor", [new SubjectTypeRef("user")])],
                [new PermissionDef("edit", new RelationRef("editor"))])],
            [new ConditionDef("within_hours",
                [new ConditionParam("start", ConditionType.Int)],
                new Compare(new ParamRef("start"), CompareOp.Le, new HourOf(new ContextNow())))]);

        var back = Json.Deserialize<Schema>(Json.Serialize(schema));

        back!.Conditions.Single().Body.ShouldBeOfType<Compare>()
            .Left.ShouldBeOfType<ParamRef>().Name.ShouldBe("start");
    }
}
