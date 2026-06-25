using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Dsl;
using Custodex.Core.Dsl.Parsing;
using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class ConditionParseTests
{
    private static ConditionDef ParseCond(string src) =>
        SchemaParser.Parse($"condition {src}").Conditions.Single();

    [Fact]
    public void Param_only_condition_attaches_empty_body()
    {
        var cond = ParseCond("legacy(n: int)");

        cond.Name.ShouldBe("legacy");
        cond.Parameters.ShouldHaveSingleItem();
        cond.Parameters[0].ShouldBe(new ConditionParam("n", ConditionType.Int));
        cond.Body.ShouldBeOfType<EmptyConditionBody>();
    }

    [Fact]
    public void Condition_with_no_params_is_allowed()
    {
        var cond = ParseCond("always_true()");

        cond.Name.ShouldBe("always_true");
        cond.Parameters.ShouldBeEmpty();
        cond.Body.ShouldBeOfType<EmptyConditionBody>();
    }

    [Fact]
    public void All_param_types_parse_correctly()
    {
        var cond = ParseCond("typed(a: bool, b: int, c: long, d: double, e: string, f: timestamp)");

        cond.Parameters.Count.ShouldBe(6);
        cond.Parameters[0].Type.ShouldBe(ConditionType.Bool);
        cond.Parameters[1].Type.ShouldBe(ConditionType.Int);
        cond.Parameters[2].Type.ShouldBe(ConditionType.Long);
        cond.Parameters[3].Type.ShouldBe(ConditionType.Double);
        cond.Parameters[4].Type.ShouldBe(ConditionType.String);
        cond.Parameters[5].Type.ShouldBe(ConditionType.Timestamp);
    }

    [Fact]
    public void Within_window_condition_parses_hour_comparison()
    {
        var cond = ParseCond("within_window(start: int, end: int) = hour(context.now) >= start && hour(context.now) < end");

        var body = cond.Body.ShouldBeOfType<BoolOp>();
        body.Op.ShouldBe(BoolConnective.And);

        var left = body.Left.ShouldBeOfType<Compare>();
        left.Op.ShouldBe(CompareOp.Ge);
        left.Left.ShouldBeOfType<HourOf>().Timestamp.ShouldBeOfType<ContextNow>();
        left.Right.ShouldBeOfType<ParamRef>().Name.ShouldBe("start");

        var right = body.Right.ShouldBeOfType<Compare>();
        right.Op.ShouldBe(CompareOp.Lt);
        right.Left.ShouldBeOfType<HourOf>().Timestamp.ShouldBeOfType<ContextNow>();
        right.Right.ShouldBeOfType<ParamRef>().Name.ShouldBe("end");
    }

    [Fact]
    public void Attribute_ref_parses_correctly()
    {
        var cond = ParseCond("at_least(n: int) = resource[\"weight\"] >= n");

        var compare = cond.Body.ShouldBeOfType<Compare>();
        compare.Op.ShouldBe(CompareOp.Ge);
        compare.Left.ShouldBeOfType<AttributeRef>().Field.ShouldBe("weight");
        compare.Right.ShouldBeOfType<ParamRef>().Name.ShouldBe("n");
    }

    [Fact]
    public void Context_subject_parses_correctly()
    {
        var cond = ParseCond("is_creator() = resource[\"created_by\"] == context.subject");

        var compare = cond.Body.ShouldBeOfType<Compare>();
        compare.Op.ShouldBe(CompareOp.Eq);
        compare.Left.ShouldBeOfType<AttributeRef>().Field.ShouldBe("created_by");
        compare.Right.ShouldBeOfType<ContextSubject>();
    }

    [Fact]
    public void Bool_literal_true_parses()
    {
        var cond = ParseCond("always(flag: bool) = true");

        cond.Body.ShouldBeOfType<LiteralBool>().Value.ShouldBeTrue();
    }

    [Fact]
    public void Bool_literal_false_parses()
    {
        var cond = ParseCond("never(flag: bool) = false");

        cond.Body.ShouldBeOfType<LiteralBool>().Value.ShouldBeFalse();
    }

    [Fact]
    public void Integer_literal_parses()
    {
        var cond = ParseCond("check() = 42");

        cond.Body.ShouldBeOfType<LiteralInt>().Value.ShouldBe(42L);
    }

    [Fact]
    public void Double_literal_parses()
    {
        var cond = ParseCond("check() = 3.14");

        cond.Body.ShouldBeOfType<LiteralDouble>().Value.ShouldBe(3.14);
    }

    [Fact]
    public void String_literal_parses()
    {
        var cond = ParseCond("check() = \"hello\"");

        cond.Body.ShouldBeOfType<LiteralString>().Value.ShouldBe("hello");
    }

    [Fact]
    public void Not_expression_parses()
    {
        var cond = ParseCond("inverted(flag: bool) = !flag");

        var not = cond.Body.ShouldBeOfType<Not>();
        not.Inner.ShouldBeOfType<ParamRef>().Name.ShouldBe("flag");
    }

    [Fact]
    public void Or_expression_parses_left_to_right()
    {
        var cond = ParseCond("either(a: bool, b: bool) = a || b");

        var boolOp = cond.Body.ShouldBeOfType<BoolOp>();
        boolOp.Op.ShouldBe(BoolConnective.Or);
    }

    [Fact]
    public void In_list_parses_correctly()
    {
        var cond = ParseCond("in_set(n: int) = n in (1, 2, 3)");

        var inList = cond.Body.ShouldBeOfType<InList>();
        inList.Item.ShouldBeOfType<ParamRef>().Name.ShouldBe("n");
        inList.Items.Count.ShouldBe(3);
        inList.Items[0].ShouldBeOfType<LiteralInt>().Value.ShouldBe(1L);
        inList.Items[1].ShouldBeOfType<LiteralInt>().Value.ShouldBe(2L);
        inList.Items[2].ShouldBeOfType<LiteralInt>().Value.ShouldBe(3L);
    }

    [Fact]
    public void Unknown_condition_type_throws_parse_exception()
    {
        Should.Throw<DslParseException>(() => SchemaParser.Parse("condition bad(x: unknown)"));
    }

    [Fact]
    public void Arithmetic_addition_parses()
    {
        var cond = ParseCond("sum(a: int, b: int) = a + b");

        var arith = cond.Body.ShouldBeOfType<Arithmetic>();
        arith.Op.ShouldBe(ArithOp.Add);
        arith.Left.ShouldBeOfType<ParamRef>().Name.ShouldBe("a");
        arith.Right.ShouldBeOfType<ParamRef>().Name.ShouldBe("b");
    }
}
