using Custodex.Core.Dsl;
using Custodex.Core.Dsl.Lexing;
using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class LexerTests
{
    [Fact]
    public void Relation_line_yields_expected_token_kinds()
    {
        var tokens = Lexer.Tokenize("relation member: user | group#member | user:*");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Relation,
            TokenKind.Ident,
            TokenKind.Colon,
            TokenKind.Ident,
            TokenKind.Pipe,
            TokenKind.Ident,
            TokenKind.Hash,
            TokenKind.Ident,
            TokenKind.Pipe,
            TokenKind.Ident,
            TokenKind.Colon,
            TokenKind.Star,
            TokenKind.Eof,
        ]);
    }

    [Fact]
    public void Permission_algebra_operators_are_tokenized()
    {
        var tokens = Lexer.Tokenize("editor + container->update - blocked & owner");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Ident,
            TokenKind.Plus,
            TokenKind.Ident,
            TokenKind.Arrow,
            TokenKind.Ident,
            TokenKind.Minus,
            TokenKind.Ident,
            TokenKind.Amp,
            TokenKind.Ident,
            TokenKind.Eof,
        ]);
    }

    [Fact]
    public void Comment_tokens_are_not_emitted()
    {
        var tokens = Lexer.Tokenize("type widget { // this is a comment\n}");

        tokens.Select(t => t.Kind).ShouldNotContain(t => t.ToString().Contains("comment"));
        tokens.Any(t => t.Text.Contains("comment")).ShouldBeFalse();
    }

    [Fact]
    public void Unexpected_character_throws_with_correct_line()
    {
        var ex = Should.Throw<DslParseException>(() => Lexer.Tokenize("type widget {\n$bad\n}"));
        ex.Line.ShouldBe(2);
    }

    [Fact]
    public void Two_char_operators_are_tokenized_correctly()
    {
        var tokens = Lexer.Tokenize("a && b || c == d != e <= f >= g");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Ident,
            TokenKind.AmpAmp,
            TokenKind.Ident,
            TokenKind.PipePipe,
            TokenKind.Ident,
            TokenKind.EqEq,
            TokenKind.Ident,
            TokenKind.BangEq,
            TokenKind.Ident,
            TokenKind.LtEq,
            TokenKind.Ident,
            TokenKind.GtEq,
            TokenKind.Ident,
            TokenKind.Eof,
        ]);
    }

    [Fact]
    public void Keywords_are_recognized_as_their_token_kinds()
    {
        var tokens = Lexer.Tokenize("type relation permission condition with in");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Type,
            TokenKind.Relation,
            TokenKind.Permission,
            TokenKind.Condition,
            TokenKind.With,
            TokenKind.In,
            TokenKind.Eof,
        ]);
    }

    [Fact]
    public void Number_literal_is_tokenized()
    {
        var tokens = Lexer.Tokenize("42 3.14");

        tokens.Select(t => t.Kind).ShouldBe([TokenKind.Number, TokenKind.Number, TokenKind.Eof]);
        tokens[0].Text.ShouldBe("42");
        tokens[1].Text.ShouldBe("3.14");
    }

    [Fact]
    public void String_literal_is_tokenized()
    {
        var tokens = Lexer.Tokenize("\"hello\"");

        tokens.Single(t => t.Kind != TokenKind.Eof).Kind.ShouldBe(TokenKind.StringLit);
        tokens.Single(t => t.Kind != TokenKind.Eof).Text.ShouldBe("hello");
    }

    [Fact]
    public void Unterminated_string_throws_parse_exception()
    {
        Should.Throw<DslParseException>(() => Lexer.Tokenize("\"unterminated"));
    }

    [Fact]
    public void Eof_token_is_always_last()
    {
        var tokens = Lexer.Tokenize("");
        tokens.ShouldHaveSingleItem();
        tokens[0].Kind.ShouldBe(TokenKind.Eof);
    }
}
