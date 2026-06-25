using Custodex.Core.Dsl.Lexing;

namespace Custodex.Core.Dsl.Parsing;

/// <summary>A cursor over a token list with peek/consume semantics.</summary>
public sealed class TokenStream
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    /// <summary>Initializes a new <see cref="TokenStream"/> over the given token list.</summary>
    public TokenStream(IReadOnlyList<Token> tokens) => _tokens = tokens;

    /// <summary>Returns the current token without advancing.</summary>
    public Token Peek() => _tokens[_pos];

    /// <summary>Returns the current token and advances to the next.</summary>
    public Token Next()
    {
        var t = _tokens[_pos];
        if (t.Kind != TokenKind.Eof) _pos++;
        return t;
    }

    /// <summary>Advances and returns the token only if it matches <paramref name="kind"/>; otherwise returns false.</summary>
    public bool Accept(TokenKind kind, out Token token)
    {
        if (_tokens[_pos].Kind == kind) { token = Next(); return true; }
        token = default;
        return false;
    }

    /// <summary>Advances and returns the token if it matches <paramref name="kind"/>; throws <see cref="DslParseException"/> otherwise.</summary>
    public Token Expect(TokenKind kind)
    {
        var t = Peek();
        if (t.Kind != kind)
            throw new DslParseException($"Expected {kind} but found '{t.Text}' ({t.Kind})", t.Line, t.Column);
        return Next();
    }
}
