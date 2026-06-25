namespace Custodex.Core.Dsl.Lexing;

internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);
