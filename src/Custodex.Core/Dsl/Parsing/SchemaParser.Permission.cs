using Custodex.Abstractions;
using Custodex.Core.Dsl.Lexing;

namespace Custodex.Core.Dsl.Parsing;

public static partial class SchemaParser
{
    private static PermissionDef ParsePermission(TokenStream stream)
    {
        stream.Expect(TokenKind.Permission);
        var name = stream.Expect(TokenKind.Ident).Text;
        stream.Expect(TokenKind.Eq);
        var expr = ParsePermExpr(stream);
        return new PermissionDef(name, expr);
    }

    private static PermExpr ParsePermExpr(TokenStream stream)
    {
        var left = ParseWithExpr(stream);

        while (true)
        {
            var t = stream.Peek();
            if (t.Kind == TokenKind.Plus)
            {
                stream.Next();
                left = new Union(left, ParseWithExpr(stream));
            }
            else if (t.Kind == TokenKind.Amp)
            {
                stream.Next();
                left = new Intersect(left, ParseWithExpr(stream));
            }
            else if (t.Kind == TokenKind.Minus)
            {
                stream.Next();
                left = new Exclude(left, ParseWithExpr(stream));
            }
            else break;
        }

        return left;
    }

    private static PermExpr ParseWithExpr(TokenStream stream)
    {
        var expr = ParsePermPrimary(stream);

        while (stream.Accept(TokenKind.With, out _))
        {
            var condName = stream.Expect(TokenKind.Ident).Text;
            expr = new Conditioned(expr, condName);
        }

        return expr;
    }

    private static PermExpr ParsePermPrimary(TokenStream stream)
    {
        if (stream.Accept(TokenKind.LParen, out _))
        {
            var inner = ParsePermExpr(stream);
            stream.Expect(TokenKind.RParen);
            return inner;
        }

        var name = stream.Expect(TokenKind.Ident).Text;

        if (stream.Accept(TokenKind.Arrow, out _))
        {
            var perm = stream.Expect(TokenKind.Ident).Text;
            return new Arrow(name, perm);
        }

        return new RelationRef(name);
    }
}
