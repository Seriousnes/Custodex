namespace Custodex.Core.Dsl.Lexing;

/// <summary>Hand-written lexer that tokenizes DSL schema text.</summary>
public static class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["type"] = TokenKind.Type,
        ["relation"] = TokenKind.Relation,
        ["permission"] = TokenKind.Permission,
        ["condition"] = TokenKind.Condition,
        ["with"] = TokenKind.With,
        ["in"] = TokenKind.In,
    };

    /// <summary>Tokenizes the given DSL source text into a list terminated by an <see cref="TokenKind.Eof"/> token.</summary>
    public static IReadOnlyList<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var pos = 0;
        var line = 1;
        var lineStart = 0;

        while (pos < source.Length)
        {
            var ch = source[pos];

            if (ch == '\n') { line++; lineStart = pos + 1; pos++; continue; }
            if (ch == '\r') { pos++; continue; }
            if (char.IsWhiteSpace(ch)) { pos++; continue; }

            if (ch == '/' && pos + 1 < source.Length && source[pos + 1] == '/')
            {
                while (pos < source.Length && source[pos] != '\n') pos++;
                continue;
            }

            var col = pos - lineStart + 1;

            if (ch == '"')
            {
                var start = pos + 1;
                pos++;
                while (pos < source.Length && source[pos] != '"' && source[pos] != '\n') pos++;
                if (pos >= source.Length || source[pos] == '\n')
                    throw new DslParseException("Unterminated string literal", line, col);
                tokens.Add(new Token(TokenKind.StringLit, source[start..pos], line, col));
                pos++;
                continue;
            }

            if (char.IsDigit(ch))
            {
                var start = pos;
                while (pos < source.Length && char.IsDigit(source[pos])) pos++;
                if (pos < source.Length && source[pos] == '.' && pos + 1 < source.Length && char.IsDigit(source[pos + 1]))
                {
                    pos++;
                    while (pos < source.Length && char.IsDigit(source[pos])) pos++;
                }
                tokens.Add(new Token(TokenKind.Number, source[start..pos], line, col));
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = pos;
                while (pos < source.Length && (char.IsLetterOrDigit(source[pos]) || source[pos] == '_')) pos++;
                var text = source[start..pos];
                var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Ident;
                tokens.Add(new Token(kind, text, line, col));
                continue;
            }

            switch (ch)
            {
                case '-' when pos + 1 < source.Length && source[pos + 1] == '>':
                    tokens.Add(new Token(TokenKind.Arrow, "->", line, col)); pos += 2; break;
                case '&' when pos + 1 < source.Length && source[pos + 1] == '&':
                    tokens.Add(new Token(TokenKind.AmpAmp, "&&", line, col)); pos += 2; break;
                case '|' when pos + 1 < source.Length && source[pos + 1] == '|':
                    tokens.Add(new Token(TokenKind.PipePipe, "||", line, col)); pos += 2; break;
                case '=' when pos + 1 < source.Length && source[pos + 1] == '=':
                    tokens.Add(new Token(TokenKind.EqEq, "==", line, col)); pos += 2; break;
                case '!' when pos + 1 < source.Length && source[pos + 1] == '=':
                    tokens.Add(new Token(TokenKind.BangEq, "!=", line, col)); pos += 2; break;
                case '<' when pos + 1 < source.Length && source[pos + 1] == '=':
                    tokens.Add(new Token(TokenKind.LtEq, "<=", line, col)); pos += 2; break;
                case '>' when pos + 1 < source.Length && source[pos + 1] == '=':
                    tokens.Add(new Token(TokenKind.GtEq, ">=", line, col)); pos += 2; break;
                case '+': tokens.Add(new Token(TokenKind.Plus, "+", line, col)); pos++; break;
                case '-': tokens.Add(new Token(TokenKind.Minus, "-", line, col)); pos++; break;
                case '*': tokens.Add(new Token(TokenKind.Star, "*", line, col)); pos++; break;
                case '/': tokens.Add(new Token(TokenKind.Slash, "/", line, col)); pos++; break;
                case '&': tokens.Add(new Token(TokenKind.Amp, "&", line, col)); pos++; break;
                case '|': tokens.Add(new Token(TokenKind.Pipe, "|", line, col)); pos++; break;
                case '!': tokens.Add(new Token(TokenKind.Bang, "!", line, col)); pos++; break;
                case '=': tokens.Add(new Token(TokenKind.Eq, "=", line, col)); pos++; break;
                case '<': tokens.Add(new Token(TokenKind.Lt, "<", line, col)); pos++; break;
                case '>': tokens.Add(new Token(TokenKind.Gt, ">", line, col)); pos++; break;
                case ':': tokens.Add(new Token(TokenKind.Colon, ":", line, col)); pos++; break;
                case '#': tokens.Add(new Token(TokenKind.Hash, "#", line, col)); pos++; break;
                case ',': tokens.Add(new Token(TokenKind.Comma, ",", line, col)); pos++; break;
                case '.': tokens.Add(new Token(TokenKind.Dot, ".", line, col)); pos++; break;
                case '(': tokens.Add(new Token(TokenKind.LParen, "(", line, col)); pos++; break;
                case ')': tokens.Add(new Token(TokenKind.RParen, ")", line, col)); pos++; break;
                case '{': tokens.Add(new Token(TokenKind.LBrace, "{", line, col)); pos++; break;
                case '}': tokens.Add(new Token(TokenKind.RBrace, "}", line, col)); pos++; break;
                case '[': tokens.Add(new Token(TokenKind.LBracket, "[", line, col)); pos++; break;
                case ']': tokens.Add(new Token(TokenKind.RBracket, "]", line, col)); pos++; break;
                default:
                    throw new DslParseException($"Unexpected character '{ch}'", line, col);
            }
        }

        tokens.Add(new Token(TokenKind.Eof, "", line, source.Length - lineStart + 1));
        return tokens;
    }
}
