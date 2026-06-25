using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Dsl.Lexing;

namespace Custodex.Core.Dsl.Parsing;

public static partial class SchemaParser
{
    private static ConditionDef ParseCondition(TokenStream stream)
    {
        stream.Expect(TokenKind.Condition);
        var name = stream.Expect(TokenKind.Ident).Text;
        stream.Expect(TokenKind.LParen);

        var parameters = new List<ConditionParam>();
        if (stream.Peek().Kind != TokenKind.RParen)
        {
            parameters.Add(ParseConditionParam(stream));
            while (stream.Accept(TokenKind.Comma, out _))
                parameters.Add(ParseConditionParam(stream));
        }

        stream.Expect(TokenKind.RParen);

        ConditionExpr body;
        if (stream.Accept(TokenKind.Eq, out _))
            body = ParseCondExpr(stream);
        else
            body = new EmptyConditionBody();

        return new ConditionDef(name, parameters, body);
    }

    private static ConditionParam ParseConditionParam(TokenStream stream)
    {
        var paramName = stream.Expect(TokenKind.Ident).Text;
        stream.Expect(TokenKind.Colon);
        var typeToken = stream.Peek();
        var condType = typeToken.Text switch
        {
            "bool" => ConditionType.Bool,
            "int" => ConditionType.Int,
            "long" => ConditionType.Long,
            "double" => ConditionType.Double,
            "string" => ConditionType.String,
            "timestamp" => ConditionType.Timestamp,
            _ => throw new DslParseException($"Unknown condition type '{typeToken.Text}'", typeToken.Line, typeToken.Column),
        };
        stream.Next();
        return new ConditionParam(paramName, condType);
    }

    private static ConditionExpr ParseCondExpr(TokenStream stream) => ParseOrExpr(stream);

    private static ConditionExpr ParseOrExpr(TokenStream stream)
    {
        var left = ParseAndExpr(stream);
        while (stream.Accept(TokenKind.PipePipe, out _))
            left = new BoolOp(left, BoolConnective.Or, ParseAndExpr(stream));
        return left;
    }

    private static ConditionExpr ParseAndExpr(TokenStream stream)
    {
        var left = ParseCmpExpr(stream);
        while (stream.Accept(TokenKind.AmpAmp, out _))
            left = new BoolOp(left, BoolConnective.And, ParseCmpExpr(stream));
        return left;
    }

    private static ConditionExpr ParseCmpExpr(TokenStream stream)
    {
        var left = ParseAddExpr(stream);
        var t = stream.Peek();
        CompareOp? op = t.Kind switch
        {
            TokenKind.EqEq => CompareOp.Eq,
            TokenKind.BangEq => CompareOp.Ne,
            TokenKind.Lt => CompareOp.Lt,
            TokenKind.LtEq => CompareOp.Le,
            TokenKind.Gt => CompareOp.Gt,
            TokenKind.GtEq => CompareOp.Ge,
            _ => null,
        };
        if (op is not null)
        {
            stream.Next();
            left = new Compare(left, op.Value, ParseAddExpr(stream));
        }
        return left;
    }

    private static ConditionExpr ParseAddExpr(TokenStream stream)
    {
        var left = ParseMulExpr(stream);
        while (true)
        {
            if (stream.Accept(TokenKind.Plus, out _))
                left = new Arithmetic(left, ArithOp.Add, ParseMulExpr(stream));
            else if (stream.Accept(TokenKind.Minus, out _))
                left = new Arithmetic(left, ArithOp.Sub, ParseMulExpr(stream));
            else break;
        }
        return left;
    }

    private static ConditionExpr ParseMulExpr(TokenStream stream)
    {
        var left = ParseUnary(stream);
        while (true)
        {
            if (stream.Accept(TokenKind.Star, out _))
                left = new Arithmetic(left, ArithOp.Mul, ParseUnary(stream));
            else if (stream.Accept(TokenKind.Slash, out _))
                left = new Arithmetic(left, ArithOp.Div, ParseUnary(stream));
            else break;
        }
        return left;
    }

    private static ConditionExpr ParseUnary(TokenStream stream)
    {
        if (stream.Accept(TokenKind.Bang, out _))
            return new Not(ParseUnary(stream));
        return ParseCondPrimary(stream);
    }

    private static ConditionExpr ParseCondPrimary(TokenStream stream)
    {
        var t = stream.Peek();

        if (t.Kind == TokenKind.LParen)
        {
            stream.Next();
            var inner = ParseCondExpr(stream);
            stream.Expect(TokenKind.RParen);
            return inner;
        }

        if (t.Kind == TokenKind.Number)
        {
            stream.Next();
            if (t.Text.Contains('.'))
                return new LiteralDouble(double.Parse(t.Text, System.Globalization.CultureInfo.InvariantCulture));
            return new LiteralInt(long.Parse(t.Text, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (t.Kind == TokenKind.StringLit)
        {
            stream.Next();
            return new LiteralString(t.Text);
        }

        if (t.Kind == TokenKind.Ident && t.Text == "true") { stream.Next(); return new LiteralBool(true); }
        if (t.Kind == TokenKind.Ident && t.Text == "false") { stream.Next(); return new LiteralBool(false); }

        if (t.Kind == TokenKind.Ident && t.Text == "resource")
        {
            stream.Next();
            stream.Expect(TokenKind.LBracket);
            var field = stream.Expect(TokenKind.StringLit).Text;
            stream.Expect(TokenKind.RBracket);
            return new AttributeRef(field);
        }

        if (t.Kind == TokenKind.Ident && t.Text == "context")
        {
            stream.Next();
            stream.Expect(TokenKind.Dot);
            var member = stream.Expect(TokenKind.Ident);
            return member.Text switch
            {
                "now" => (ConditionExpr)new ContextNow(),
                "subject" => new ContextSubject(),
                _ => throw new DslParseException($"Unknown context member '{member.Text}'", member.Line, member.Column),
            };
        }

        if (t.Kind == TokenKind.Ident && t.Text == "hour")
        {
            stream.Next();
            stream.Expect(TokenKind.LParen);
            var arg = ParseCondExpr(stream);
            stream.Expect(TokenKind.RParen);
            return new HourOf(arg);
        }

        if (t.Kind == TokenKind.Ident)
        {
            stream.Next();
            if (stream.Peek().Kind == TokenKind.In)
            {
                stream.Next();
                stream.Expect(TokenKind.LParen);
                var items = new List<ConditionExpr> { ParseCondExpr(stream) };
                while (stream.Accept(TokenKind.Comma, out _))
                    items.Add(ParseCondExpr(stream));
                stream.Expect(TokenKind.RParen);
                return new InList(new ParamRef(t.Text), items);
            }
            return new ParamRef(t.Text);
        }

        throw new DslParseException($"Unexpected token '{t.Text}' in condition expression", t.Line, t.Column);
    }
}
