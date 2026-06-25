namespace Custodex.Core.Dsl.Lexing;

/// <summary>Identifies the syntactic category of a DSL token.</summary>
public enum TokenKind
{
    Eof,
    Ident,
    Number,
    StringLit,

    Type,
    Relation,
    Permission,
    Condition,
    With,
    In,

    Plus,
    Minus,
    Star,
    Slash,
    Amp,
    Pipe,
    Bang,
    Eq,
    Lt,
    Gt,
    Colon,
    Hash,
    Comma,
    Dot,
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,

    Arrow,
    AmpAmp,
    PipePipe,
    EqEq,
    BangEq,
    LtEq,
    GtEq,
}
