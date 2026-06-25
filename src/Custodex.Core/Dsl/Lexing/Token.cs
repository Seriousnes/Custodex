namespace Custodex.Core.Dsl.Lexing;

/// <summary>A single lexical token produced by the DSL lexer.</summary>
public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);
