using Custodex.Core.Dsl.Lexing;

namespace Custodex.Core.Dsl.Parsing;

internal sealed class TokenStream
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    internal TokenStream(IReadOnlyList<Token> tokens) => _tokens = tokens;

    internal Token Peek() => _tokens[_pos];

    internal Token Next()
    {
        var t = _tokens[_pos];
        if (t.Kind != TokenKind.Eof) _pos++;
        return t;
    }

    internal bool Accept(TokenKind kind, out Token token)
    {
        if (_tokens[_pos].Kind == kind) { token = Next(); return true; }
        token = default;
        return false;
    }

    internal Token Expect(TokenKind kind)
    {
        var t = Peek();
        if (t.Kind != kind)
            throw new DslParseException($"Expected {kind} but found '{t.Text}' ({t.Kind})", t.Line, t.Column);
        return Next();
    }
}
