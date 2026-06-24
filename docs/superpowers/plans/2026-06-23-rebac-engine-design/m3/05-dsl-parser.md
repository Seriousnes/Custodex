# M3/05 — DSL Parser & Serializer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A text DSL ⇄ canonical `Schema` round-trip. A Permify/OpenFGA-style schema text parses into the **exact** `Custodex.Abstractions` `Schema`/`PermExpr` AST (`Union`=`+`, `Intersect`=`&`, `Exclude`=`-`, `Arrow`=`rel->perm`, `Conditioned`=`expr with cond`), and a serializer renders any `Schema` back to that text. The property `parse(serialize(schema)) == schema` holds for generated schemas, and the `m0/02` animal schema parses to the **same AST the `SchemaBuilder` produces**.

**Architecture:** A hand-written recursive-descent parser in a new `Custodex.Dsl` package (`Custodex.Core` reference for nothing structural; it targets `Custodex.Abstractions` AST + `Custodex.Core.Conditions` body nodes). Three layers: a `Lexer` (tokens: identifiers, the operators `+ & - -> | * : # ( ) = ,`, keywords `type relation permission condition with`, and condition operators), a `SchemaParser` producing `Schema`, and a `SchemaWriter` rendering `Schema` to text. The permission expression grammar fixes precedence so the parse tree matches the builder's left-associative accumulation: `Union` (`+`) and `Exclude` (`-`) and `Intersect` (`&`) are parsed left-to-right at one precedence tier (matching `m0/02`'s "default chaining, then wrap" builder), `Arrow` (`->`) and parenthesized groups are primaries, and `with` (Conditioned) binds tightest to its left operand. The serializer is the inverse and is parenthesis-minimal but round-trip-faithful.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly, **CsCheck** (MIT, for the round-trip property).

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 license metadata; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. Depends on `m0/01` (`Schema`/`PermExpr` AST), `m0/02` (`SchemaBuilder`, for the animal-schema parity test), and `m0/06` (the concrete `ConditionExpr` body nodes — `LiteralInt`, `ParamRef`, `AttributeRef`, `ContextNow`, `Compare`, `BoolOp`, `HourOf`, etc.).

## DSL grammar (normative)

The DSL is line-oriented within blocks; whitespace is insignificant except as a token separator. `//` begins a line comment.

```
schema      := (typeDecl | conditionDecl)*

typeDecl    := "type" ident "{" member* "}"
member      := relationDecl | permissionDecl
relationDecl   := "relation" ident ":" subjectList
subjectList    := subject ("|" subject)*
subject     := ident                       // any instance of a type, e.g. user            -> SubjectTypeRef(type, null, false)
             | ident ":" "*"               // wildcard, e.g. user:*                          -> SubjectTypeRef(type, null, true)
             | ident "#" ident             // subject-set, e.g. group#member                -> SubjectTypeRef(type, relation, false)

permissionDecl := "permission" ident "=" permExpr

// Permission algebra. One left-associative tier for + & - so the tree matches the
// SchemaBuilder accumulation (m0/02): terms combine left-to-right by their operator.
permExpr    := withExpr (("+" | "&" | "-") withExpr)*
withExpr    := primary ("with" ident)*     // Conditioned wraps its left operand
primary     := ident                       // RelationRef(ident)  (a relation OR a nested permission name)
             | ident "->" ident            // Arrow(relation, permission)
             | "(" permExpr ")"

conditionDecl  := "condition" ident "(" paramList? ")" ("=" condExpr)?
paramList   := param ("," param)*
param       := ident ":" condType
condType    := "bool" | "int" | "long" | "double" | "string" | "timestamp"

// Condition body (CEL-shaped, m0/06). Standard precedence: || < && < comparison < +/- < */ < unary < primary.
condExpr    := orExpr
orExpr      := andExpr ("||" andExpr)*
andExpr     := cmpExpr ("&&" cmpExpr)*
cmpExpr     := addExpr (("==" | "!=" | "<" | "<=" | ">" | ">=") addExpr)?
addExpr     := mulExpr (("+" | "-") mulExpr)*
mulExpr     := unary (("*" | "/") unary)*
unary       := "!" unary | condPrimary
condPrimary := number | string | "true" | "false"
             | ident                         // ParamRef(ident)
             | "resource" "[" string "]"      // AttributeRef
             | "context" "." "now"            // ContextNow
             | "context" "." "subject"        // ContextSubject
             | "hour" "(" condExpr ")"        // HourOf
             | ident "in" "(" condExpr ("," condExpr)* ")"   // InList (item is the leading ident)
             | "(" condExpr ")"
```

**Algebra-tier precedence is deliberate.** `+ & -` share one left-associative tier so `a + b - c` parses as `Exclude(Union(a, b), c)` — exactly the tree `new PermExprBuilder().Relation("a").Union(...).Exclude(...)` builds in `m0/02`. A consumer who needs `a + (b - c)` writes the parentheses; the serializer emits them only when the tree requires them.

---

### Task 1: Create `Custodex.Dsl` and the lexer

**Files:**
- Create: `src/Custodex.Dsl/Custodex.Dsl.csproj`
- Create: `src/Custodex.Dsl/Lexing/Token.cs`
- Create: `src/Custodex.Dsl/Lexing/Lexer.cs`
- Create: `src/Custodex.Dsl/DslParseException.cs`
- Create: `tests/Custodex.Dsl.Tests/Custodex.Dsl.Tests.csproj`
- Test: `tests/Custodex.Dsl.Tests/LexerTests.cs`

**Interfaces:**
- Produces: `enum TokenKind`; `readonly record struct Token(TokenKind Kind, string Text, int Line, int Column)`; `Lexer.Tokenize(string source) -> IReadOnlyList<Token>` (terminated by an `Eof` token); `DslParseException(string message, int line, int column)`.
- Consumes: nothing beyond BCL.

- [ ] **Step 1: Create the project and references**

Run:
```bash
dotnet new classlib -n Custodex.Dsl -o src/Custodex.Dsl -f net10.0
dotnet new xunit -n Custodex.Dsl.Tests -o tests/Custodex.Dsl.Tests -f net10.0
rm src/Custodex.Dsl/Class1.cs tests/Custodex.Dsl.Tests/UnitTest1.cs
dotnet sln add src/Custodex.Dsl tests/Custodex.Dsl.Tests
dotnet add src/Custodex.Dsl reference src/Custodex.Abstractions
dotnet add src/Custodex.Dsl reference src/Custodex.Core
dotnet add tests/Custodex.Dsl.Tests reference src/Custodex.Dsl
dotnet add tests/Custodex.Dsl.Tests reference src/Custodex.Core
dotnet add tests/Custodex.Dsl.Tests package Shouldly
dotnet add tests/Custodex.Dsl.Tests package CsCheck
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Custodex.Dsl.Tests/LexerTests.cs
using Custodex.Dsl.Lexing;
using Shouldly;
using Xunit;

namespace Custodex.Dsl.Tests;

public class LexerTests
{
    [Fact]
    public void Lexes_a_relation_declaration_with_subject_set_and_wildcard()
    {
        var tokens = Lexer.Tokenize("relation member: user | group#member | user:*");
        var kinds = tokens.Select(t => t.Kind).ToList();
        kinds.ShouldContain(TokenKind.Relation);
        kinds.ShouldContain(TokenKind.Pipe);
        kinds.ShouldContain(TokenKind.Hash);
        kinds.ShouldContain(TokenKind.Star);
        tokens[^1].Kind.ShouldBe(TokenKind.Eof);
    }

    [Fact]
    public void Lexes_the_arrow_and_algebra_operators()
    {
        var tokens = Lexer.Tokenize("medicator + enclosure->edit - blocked & vet");
        var kinds = tokens.Select(t => t.Kind).ToList();
        kinds.ShouldContain(TokenKind.Plus);
        kinds.ShouldContain(TokenKind.Arrow);
        kinds.ShouldContain(TokenKind.Minus);
        kinds.ShouldContain(TokenKind.Amp);
    }

    [Fact]
    public void Skips_line_comments()
    {
        var tokens = Lexer.Tokenize("type animal { // a comment\n }");
        tokens.Any(t => t.Text.Contains("comment")).ShouldBeFalse();
    }

    [Fact]
    public void Reports_line_and_column_on_an_unexpected_character()
    {
        var ex = Should.Throw<DslParseException>(() => Lexer.Tokenize("type animal {\n  relation x: $bad }"));
        ex.Line.ShouldBe(2);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter LexerTests`
Expected: FAIL — `Lexer`/`Token` not defined.

- [ ] **Step 4: Implement the token model and exception**

```csharp
// src/Custodex.Dsl/DslParseException.cs
namespace Custodex.Dsl;

public sealed class DslParseException(string message, int line, int column)
    : Exception($"{message} (line {line}, column {column})")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}
```

```csharp
// src/Custodex.Dsl/Lexing/Token.cs
namespace Custodex.Dsl.Lexing;

public enum TokenKind
{
    // literals / names
    Ident, Number, String,
    // keywords
    Type, Relation, Permission, Condition, With, In,
    // punctuation / operators
    LBrace, RBrace, LParen, RParen, LBracket, RBracket,
    Colon, Hash, Pipe, Star, Equals, Comma, Dot,
    Plus, Minus, Amp, Arrow,                 // permission algebra (+ - & ->)
    Bang, AndAnd, OrOr, EqEq, NotEq, Lt, Le, Gt, Ge,   // condition operators
    Slash,                                   // division in condition bodies
    Eof,
}

public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);
```

```csharp
// src/Custodex.Dsl/Lexing/Lexer.cs
using System.Text;

namespace Custodex.Dsl.Lexing;

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

    public static IReadOnlyList<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int line = 1, col = 1, i = 0;

        void Advance() { col++; i++; }

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\n') { line++; col = 1; i++; continue; }
            if (char.IsWhiteSpace(c)) { Advance(); continue; }

            // line comment
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            int startCol = col;

            // string literal
            if (c == '"')
            {
                var sb = new StringBuilder();
                Advance();
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\n') throw new DslParseException("unterminated string", line, startCol);
                    sb.Append(source[i]); Advance();
                }
                if (i >= source.Length) throw new DslParseException("unterminated string", line, startCol);
                Advance(); // closing quote
                tokens.Add(new Token(TokenKind.String, sb.ToString(), line, startCol));
                continue;
            }

            // number (int or double)
            if (char.IsDigit(c))
            {
                var sb = new StringBuilder();
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.')) { sb.Append(source[i]); Advance(); }
                tokens.Add(new Token(TokenKind.Number, sb.ToString(), line, startCol));
                continue;
            }

            // identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                var sb = new StringBuilder();
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '-'))
                { sb.Append(source[i]); Advance(); }
                var text = sb.ToString();
                var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Ident;
                tokens.Add(new Token(kind, text, line, startCol));
                continue;
            }

            // operators / punctuation (two-char first)
            TokenKind Two(char next, TokenKind two, TokenKind one)
            {
                if (i + 1 < source.Length && source[i + 1] == next) { Advance(); Advance(); return two; }
                Advance(); return one;
            }

            Token? op = c switch
            {
                '{' => Emit(TokenKind.LBrace), '}' => Emit(TokenKind.RBrace),
                '(' => Emit(TokenKind.LParen), ')' => Emit(TokenKind.RParen),
                '[' => Emit(TokenKind.LBracket), ']' => Emit(TokenKind.RBracket),
                ':' => Emit(TokenKind.Colon), '#' => Emit(TokenKind.Hash),
                '|' => i + 1 < source.Length && source[i + 1] == '|' ? Emit2(TokenKind.OrOr) : Emit(TokenKind.Pipe),
                '*' => Emit(TokenKind.Star), '=' => i + 1 < source.Length && source[i + 1] == '=' ? Emit2(TokenKind.EqEq) : Emit(TokenKind.Equals),
                ',' => Emit(TokenKind.Comma), '.' => Emit(TokenKind.Dot),
                '+' => Emit(TokenKind.Plus),
                '-' => i + 1 < source.Length && source[i + 1] == '>' ? Emit2(TokenKind.Arrow) : Emit(TokenKind.Minus),
                '&' => i + 1 < source.Length && source[i + 1] == '&' ? Emit2(TokenKind.AndAnd) : Emit(TokenKind.Amp),
                '!' => i + 1 < source.Length && source[i + 1] == '=' ? Emit2(TokenKind.NotEq) : Emit(TokenKind.Bang),
                '<' => i + 1 < source.Length && source[i + 1] == '=' ? Emit2(TokenKind.Le) : Emit(TokenKind.Lt),
                '>' => i + 1 < source.Length && source[i + 1] == '=' ? Emit2(TokenKind.Ge) : Emit(TokenKind.Gt),
                '/' => Emit(TokenKind.Slash),
                _ => null,
            };

            if (op is null) throw new DslParseException($"unexpected character '{c}'", line, startCol);
            tokens.Add(op.Value);
            continue;

            Token Emit(TokenKind k) { var t = new Token(k, source[i].ToString(), line, startCol); Advance(); return t; }
            Token Emit2(TokenKind k) { var t = new Token(k, source.Substring(i, 2), line, startCol); Advance(); Advance(); return t; }
        }

        tokens.Add(new Token(TokenKind.Eof, "", line, col));
        return tokens;
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter LexerTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Dsl tests/Custodex.Dsl.Tests
git commit -m "feat: add Custodex.Dsl lexer and token model"
```

---

### Task 2: Parse relations and subject lists

**Files:**
- Create: `src/Custodex.Dsl/Parsing/TokenStream.cs`
- Create: `src/Custodex.Dsl/Parsing/SchemaParser.cs`
- Test: `tests/Custodex.Dsl.Tests/RelationParseTests.cs`

**Interfaces:**
- Produces: `TokenStream` (a cursor over the token list with `Peek`/`Next`/`Expect(TokenKind)`); `SchemaParser.Parse(string source) -> Schema`. This task implements `type`/`relation` parsing only; permissions and conditions follow in Tasks 3–4 (a permission/condition member throws "not yet" until then — but author the full parser dispatch now and fill members incrementally).
- Consumes: `Lexer`, `Token`, `TokenKind`; `Schema`, `EntityTypeDef`, `RelationDef`, `SubjectTypeRef` from `Custodex.Abstractions`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Dsl.Tests/RelationParseTests.cs
using Custodex.Abstractions;
using Custodex.Dsl.Parsing;
using Shouldly;
using Xunit;

namespace Custodex.Dsl.Tests;

public class RelationParseTests
{
    [Fact]
    public void Parses_a_type_with_relations_including_subject_set_and_wildcard()
    {
        const string src = """
            type group {
              relation member: user | group#member
            }
            type enclosure {
              relation is_quarantine: user:*
            }
            """;
        var schema = SchemaParser.Parse(src);

        var group = schema.Types.Single(t => t.Name == "group");
        var member = group.Relations.Single();
        member.Name.ShouldBe("member");
        member.AllowedSubjects.ShouldContain(new SubjectTypeRef("user", null, false));
        member.AllowedSubjects.ShouldContain(new SubjectTypeRef("group", "member", false));

        var enclosure = schema.Types.Single(t => t.Name == "enclosure");
        enclosure.Relations.Single().AllowedSubjects.ShouldContain(new SubjectTypeRef("user", null, true));
    }

    [Fact]
    public void Empty_schema_parses_to_no_types()
    {
        SchemaParser.Parse("").Types.ShouldBeEmpty();
    }

    [Fact]
    public void Schema_version_defaults_when_no_explicit_version()
    {
        SchemaParser.Parse("type x { }").Version.ShouldBe("v1");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter RelationParseTests`
Expected: FAIL — `SchemaParser` not defined.

- [ ] **Step 3: Implement the token stream and parser skeleton**

```csharp
// src/Custodex.Dsl/Parsing/TokenStream.cs
using Custodex.Dsl.Lexing;

namespace Custodex.Dsl.Parsing;

public sealed class TokenStream(IReadOnlyList<Token> tokens)
{
    private int _pos;

    public Token Peek => tokens[_pos];
    public Token PeekAt(int ahead) => tokens[Math.Min(_pos + ahead, tokens.Count - 1)];
    public bool At(TokenKind kind) => Peek.Kind == kind;

    public Token Next() => tokens[_pos++];

    public bool Accept(TokenKind kind)
    {
        if (Peek.Kind != kind) return false;
        _pos++;
        return true;
    }

    public Token Expect(TokenKind kind)
    {
        if (Peek.Kind != kind)
            throw new DslParseException($"expected {kind} but found {Peek.Kind} '{Peek.Text}'", Peek.Line, Peek.Column);
        return tokens[_pos++];
    }
}
```

```csharp
// src/Custodex.Dsl/Parsing/SchemaParser.cs
using Custodex.Abstractions;
using Custodex.Dsl.Lexing;

namespace Custodex.Dsl.Parsing;

public static partial class SchemaParser
{
    public const string DefaultVersion = "v1";

    public static Schema Parse(string source) => Parse(source, DefaultVersion);

    public static Schema Parse(string source, string version)
    {
        var stream = new TokenStream(Lexer.Tokenize(source));
        var types = new List<EntityTypeDef>();
        var conditions = new List<ConditionDef>();

        while (!stream.At(TokenKind.Eof))
        {
            if (stream.Accept(TokenKind.Type)) types.Add(ParseType(stream));
            else if (stream.At(TokenKind.Condition)) conditions.Add(ParseCondition(stream));
            else throw new DslParseException(
                $"expected 'type' or 'condition' but found '{stream.Peek.Text}'", stream.Peek.Line, stream.Peek.Column);
        }

        return new Schema(version, types, conditions);
    }

    private static EntityTypeDef ParseType(TokenStream s)
    {
        var name = s.Expect(TokenKind.Ident).Text;
        s.Expect(TokenKind.LBrace);

        var relations = new List<RelationDef>();
        var permissions = new List<PermissionDef>();

        while (!s.At(TokenKind.RBrace) && !s.At(TokenKind.Eof))
        {
            if (s.Accept(TokenKind.Relation)) relations.Add(ParseRelation(s));
            else if (s.Accept(TokenKind.Permission)) permissions.Add(ParsePermission(s));
            else throw new DslParseException(
                $"expected 'relation' or 'permission' but found '{s.Peek.Text}'", s.Peek.Line, s.Peek.Column);
        }

        s.Expect(TokenKind.RBrace);
        return new EntityTypeDef(name, relations, permissions);
    }

    private static RelationDef ParseRelation(TokenStream s)
    {
        var name = s.Expect(TokenKind.Ident).Text;
        s.Expect(TokenKind.Colon);

        var subjects = new List<SubjectTypeRef> { ParseSubject(s) };
        while (s.Accept(TokenKind.Pipe))
            subjects.Add(ParseSubject(s));

        return new RelationDef(name, subjects);
    }

    private static SubjectTypeRef ParseSubject(TokenStream s)
    {
        var type = s.Expect(TokenKind.Ident).Text;
        if (s.Accept(TokenKind.Hash))
        {
            var relation = s.Expect(TokenKind.Ident).Text;
            return new SubjectTypeRef(type, relation, false);
        }
        if (s.Accept(TokenKind.Colon))
        {
            s.Expect(TokenKind.Star);
            return new SubjectTypeRef(type, null, true);
        }
        return new SubjectTypeRef(type, null, false);
    }
}
```

> `ParsePermission` and `ParseCondition` are added in Tasks 3–4 (the `partial class` lets each task add its members in its own file). For this task, add a temporary stub in `SchemaParser` so it compiles:

```csharp
// src/Custodex.Dsl/Parsing/SchemaParser.Stubs.cs   (DELETED in Tasks 3 and 4 as the real members land)
using Custodex.Abstractions;
using Custodex.Dsl.Lexing;

namespace Custodex.Dsl.Parsing;

public static partial class SchemaParser
{
    private static PermissionDef ParsePermission(TokenStream s) =>
        throw new DslParseException("permissions not parsed yet (Task 3)", s.Peek.Line, s.Peek.Column);

    private static ConditionDef ParseCondition(TokenStream s) =>
        throw new DslParseException("conditions not parsed yet (Task 4)", s.Peek.Line, s.Peek.Column);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter RelationParseTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Dsl tests/Custodex.Dsl.Tests
git commit -m "feat: parse types, relations, and subject lists from DSL"
```

---

### Task 3: Parse the permission algebra to the exact AST

**Files:**
- Create: `src/Custodex.Dsl/Parsing/SchemaParser.Permission.cs`
- Delete: `src/Custodex.Dsl/Parsing/SchemaParser.Stubs.cs` (replace the permission stub)
- Test: `tests/Custodex.Dsl.Tests/PermissionParseTests.cs`

**Interfaces:**
- Produces: `SchemaParser.ParsePermission(TokenStream) -> PermissionDef` building `RelationRef`/`Union`/`Intersect`/`Exclude`/`Arrow`/`Conditioned` matching the operators `+ & - -> with`.
- Consumes: `PermExpr` and subtypes from `Custodex.Abstractions`.

> **The parse tree must match the `SchemaBuilder` output** for the animal permission `medicator + enclosure->edit - blocked`: `Exclude(Union(RelationRef "medicator", Arrow("enclosure","edit")), RelationRef "blocked")`. The single left-associative tier for `+ & -` (the builder's "default-chain then wrap" semantics) produces exactly that. `with` binds to its immediate left primary (Conditioned wraps the tightest operand).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Dsl.Tests/PermissionParseTests.cs
using Custodex.Abstractions;
using Custodex.Dsl.Parsing;
using Shouldly;
using Xunit;

namespace Custodex.Dsl.Tests;

public class PermissionParseTests
{
    private static PermExpr ParseExpr(string permBody)
    {
        var schema = SchemaParser.Parse($$"""
            type t {
              relation a: user
              relation b: user
              relation blocked: user
              relation medicator: user
              relation enclosure: enclosure
              permission edit = {{permBody}}
            }
            """);
        return schema.Types.Single().Permissions.Single(p => p.Name == "edit").Expression;
    }

    [Fact]
    public void Union_exclude_chain_matches_builder_left_association()
    {
        // medicator + enclosure->edit - blocked  =>  Exclude(Union(RelationRef, Arrow), RelationRef)
        var expr = ParseExpr("medicator + enclosure->edit - blocked");

        var exclude = expr.ShouldBeOfType<Exclude>();
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("blocked");
        var union = exclude.Left.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("medicator");
        var arrow = union.Right.ShouldBeOfType<Arrow>();
        arrow.Relation.ShouldBe("enclosure");
        arrow.Permission.ShouldBe("edit");
    }

    [Fact]
    public void Intersect_parses_to_Intersect_node()
    {
        var expr = ParseExpr("a & b");
        var inter = expr.ShouldBeOfType<Intersect>();
        inter.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("a");
        inter.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("b");
    }

    [Fact]
    public void Parentheses_override_left_association()
    {
        // a + (b - blocked)  =>  Union(RelationRef a, Exclude(RelationRef b, RelationRef blocked))
        var expr = ParseExpr("a + (b - blocked)");
        var union = expr.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("a");
        union.Right.ShouldBeOfType<Exclude>().Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("b");
    }

    [Fact]
    public void With_wraps_left_operand_in_Conditioned()
    {
        var expr = ParseExpr("a with within_hours");
        var cond = expr.ShouldBeOfType<Conditioned>();
        cond.ConditionName.ShouldBe("within_hours");
        cond.Inner.ShouldBeOfType<RelationRef>().Relation.ShouldBe("a");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter PermissionParseTests`
Expected: FAIL — `ParsePermission` still throws the "not parsed yet" stub.

- [ ] **Step 3: Remove the permission stub and implement the algebra parser**

```bash
# Remove only the permission stub method. If both stubs are in SchemaParser.Stubs.cs, keep the
# condition stub there until Task 4; here we delete the permission stub line.
```

Edit `src/Custodex.Dsl/Parsing/SchemaParser.Stubs.cs` to drop `ParsePermission` (leave `ParseCondition`), then add:

```csharp
// src/Custodex.Dsl/Parsing/SchemaParser.Permission.cs
using Custodex.Abstractions;
using Custodex.Dsl.Lexing;

namespace Custodex.Dsl.Parsing;

public static partial class SchemaParser
{
    private static PermissionDef ParsePermission(TokenStream s)
    {
        var name = s.Expect(TokenKind.Ident).Text;
        s.Expect(TokenKind.Equals);
        var expr = ParsePermExpr(s);
        return new PermissionDef(name, expr);
    }

    // permExpr := withExpr (("+" | "&" | "-") withExpr)*   — one left-associative tier
    private static PermExpr ParsePermExpr(TokenStream s)
    {
        var left = ParseWithExpr(s);
        while (true)
        {
            if (s.Accept(TokenKind.Plus)) left = new Union(left, ParseWithExpr(s));
            else if (s.Accept(TokenKind.Amp)) left = new Intersect(left, ParseWithExpr(s));
            else if (s.Accept(TokenKind.Minus)) left = new Exclude(left, ParseWithExpr(s));
            else break;
        }
        return left;
    }

    // withExpr := primary ("with" ident)*
    private static PermExpr ParseWithExpr(TokenStream s)
    {
        var primary = ParsePermPrimary(s);
        while (s.Accept(TokenKind.With))
        {
            var conditionName = s.Expect(TokenKind.Ident).Text;
            primary = new Conditioned(primary, conditionName);
        }
        return primary;
    }

    // primary := "(" permExpr ")" | ident "->" ident (Arrow) | ident (RelationRef)
    private static PermExpr ParsePermPrimary(TokenStream s)
    {
        if (s.Accept(TokenKind.LParen))
        {
            var inner = ParsePermExpr(s);
            s.Expect(TokenKind.RParen);
            return inner;
        }

        var ident = s.Expect(TokenKind.Ident).Text;
        if (s.Accept(TokenKind.Arrow))
        {
            var permission = s.Expect(TokenKind.Ident).Text;
            return new Arrow(ident, permission);
        }
        return new RelationRef(ident);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter PermissionParseTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Dsl tests/Custodex.Dsl.Tests
git commit -m "feat: parse permission algebra to the exact PermExpr AST"
```

---

### Task 4: Parse conditions and their CEL-shaped bodies

**Files:**
- Create: `src/Custodex.Dsl/Parsing/SchemaParser.Condition.cs`
- Delete: `src/Custodex.Dsl/Parsing/SchemaParser.Stubs.cs` (replace the condition stub)
- Test: `tests/Custodex.Dsl.Tests/ConditionParseTests.cs`

**Interfaces:**
- Produces: `SchemaParser.ParseCondition(TokenStream) -> ConditionDef`, parsing the param list (`name: type`) and an optional `= body` whose AST uses the `m0/06` nodes (`Compare`, `BoolOp`, `Not`, `Arithmetic`, `InList`, `HourOf`, `ParamRef`, `AttributeRef`, `ContextNow`, `ContextSubject`, literals). A param-only condition with no body attaches `EmptyConditionBody` (from `m0/02`).
- Consumes: `ConditionDef`, `ConditionParam`, `ConditionType`, `ConditionExpr` (`m0/01`); the body nodes + `EmptyConditionBody` (`m0/06`/`m0/02`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Dsl.Tests/ConditionParseTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Dsl.Parsing;
using Shouldly;
using Xunit;

namespace Custodex.Dsl.Tests;

public class ConditionParseTests
{
    private static ConditionDef ParseCond(string src) => SchemaParser.Parse(src).Conditions.Single();

    [Fact]
    public void Parses_params_and_within_hours_body()
    {
        var def = ParseCond(
            "condition within_hours(start: int, end: int) = hour(context.now) >= start && hour(context.now) < end");

        def.Name.ShouldBe("within_hours");
        def.Parameters.ShouldBe(new[]
        {
            new ConditionParam("start", ConditionType.Int),
            new ConditionParam("end", ConditionType.Int),
        });

        var top = def.Body.ShouldBeOfType<BoolOp>();
        top.Op.ShouldBe(BoolConnective.And);
        var left = top.Left.ShouldBeOfType<Compare>();
        left.Op.ShouldBe(CompareOp.Ge);
        left.Left.ShouldBeOfType<HourOf>().Timestamp.ShouldBeOfType<ContextNow>();
        left.Right.ShouldBeOfType<ParamRef>().Name.ShouldBe("start");
    }

    [Fact]
    public void Parses_at_least_with_resource_attribute()
    {
        var def = ParseCond("condition at_least(n: int) = resource[\"weight\"] >= n");
        var cmp = def.Body.ShouldBeOfType<Compare>();
        cmp.Left.ShouldBeOfType<AttributeRef>().Field.ShouldBe("weight");
        cmp.Right.ShouldBeOfType<ParamRef>().Name.ShouldBe("n");
    }

    [Fact]
    public void Parses_is_creator_against_context_subject()
    {
        var def = ParseCond("condition is_creator() = resource[\"created_by\"] == context.subject");
        var cmp = def.Body.ShouldBeOfType<Compare>();
        cmp.Op.ShouldBe(CompareOp.Eq);
        cmp.Left.ShouldBeOfType<AttributeRef>().Field.ShouldBe("created_by");
        cmp.Right.ShouldBeOfType<ContextSubject>();
    }

    [Fact]
    public void Param_only_condition_has_empty_body()
    {
        var def = ParseCond("condition legacy(n: int)");
        def.Body.ShouldBeOfType<EmptyConditionBody>();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter ConditionParseTests`
Expected: FAIL — `ParseCondition` still throws the stub.

- [ ] **Step 3: Delete the stub file and implement the condition parser**

```bash
rm src/Custodex.Dsl/Parsing/SchemaParser.Stubs.cs
```

```csharp
// src/Custodex.Dsl/Parsing/SchemaParser.Condition.cs
using System.Globalization;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Dsl.Lexing;

namespace Custodex.Dsl.Parsing;

public static partial class SchemaParser
{
    private static ConditionDef ParseCondition(TokenStream s)
    {
        s.Expect(TokenKind.Condition);
        var name = s.Expect(TokenKind.Ident).Text;
        s.Expect(TokenKind.LParen);

        var @params = new List<ConditionParam>();
        if (!s.At(TokenKind.RParen))
        {
            @params.Add(ParseParam(s));
            while (s.Accept(TokenKind.Comma)) @params.Add(ParseParam(s));
        }
        s.Expect(TokenKind.RParen);

        ConditionExpr body = s.Accept(TokenKind.Equals) ? ParseCondExpr(s) : new EmptyConditionBody();
        return new ConditionDef(name, @params, body);
    }

    private static ConditionParam ParseParam(TokenStream s)
    {
        var name = s.Expect(TokenKind.Ident).Text;
        s.Expect(TokenKind.Colon);
        var typeName = s.Expect(TokenKind.Ident).Text;
        var type = typeName switch
        {
            "bool" => ConditionType.Bool,
            "int" => ConditionType.Int,
            "long" => ConditionType.Long,
            "double" => ConditionType.Double,
            "string" => ConditionType.String,
            "timestamp" => ConditionType.Timestamp,
            _ => throw new DslParseException($"unknown condition type '{typeName}'", s.Peek.Line, s.Peek.Column),
        };
        return new ConditionParam(name, type);
    }

    private static ConditionExpr ParseCondExpr(TokenStream s) => ParseOr(s);

    private static ConditionExpr ParseOr(TokenStream s)
    {
        var left = ParseAnd(s);
        while (s.Accept(TokenKind.OrOr)) left = new BoolOp(left, BoolConnective.Or, ParseAnd(s));
        return left;
    }

    private static ConditionExpr ParseAnd(TokenStream s)
    {
        var left = ParseCompare(s);
        while (s.Accept(TokenKind.AndAnd)) left = new BoolOp(left, BoolConnective.And, ParseCompare(s));
        return left;
    }

    private static ConditionExpr ParseCompare(TokenStream s)
    {
        var left = ParseAdd(s);
        CompareOp? op = s.Peek.Kind switch
        {
            TokenKind.EqEq => CompareOp.Eq, TokenKind.NotEq => CompareOp.Ne,
            TokenKind.Lt => CompareOp.Lt, TokenKind.Le => CompareOp.Le,
            TokenKind.Gt => CompareOp.Gt, TokenKind.Ge => CompareOp.Ge,
            _ => null,
        };
        if (op is null) return left;
        s.Next();
        return new Compare(left, op.Value, ParseAdd(s));
    }

    private static ConditionExpr ParseAdd(TokenStream s)
    {
        var left = ParseMul(s);
        while (true)
        {
            if (s.Accept(TokenKind.Plus)) left = new Arithmetic(left, ArithOp.Add, ParseMul(s));
            else if (s.Accept(TokenKind.Minus)) left = new Arithmetic(left, ArithOp.Sub, ParseMul(s));
            else break;
        }
        return left;
    }

    private static ConditionExpr ParseMul(TokenStream s)
    {
        var left = ParseUnary(s);
        while (true)
        {
            if (s.Accept(TokenKind.Star)) left = new Arithmetic(left, ArithOp.Mul, ParseUnary(s));
            else if (s.Accept(TokenKind.Slash)) left = new Arithmetic(left, ArithOp.Div, ParseUnary(s));
            else break;
        }
        return left;
    }

    private static ConditionExpr ParseUnary(TokenStream s)
    {
        if (s.Accept(TokenKind.Bang)) return new Not(ParseUnary(s));
        return ParseCondPrimary(s);
    }

    private static ConditionExpr ParseCondPrimary(TokenStream s)
    {
        var tok = s.Peek;

        if (s.Accept(TokenKind.LParen))
        {
            var inner = ParseCondExpr(s);
            s.Expect(TokenKind.RParen);
            return inner;
        }

        if (tok.Kind == TokenKind.Number)
        {
            s.Next();
            return tok.Text.Contains('.')
                ? new LiteralDouble(double.Parse(tok.Text, CultureInfo.InvariantCulture))
                : new LiteralInt(long.Parse(tok.Text, CultureInfo.InvariantCulture));
        }

        if (tok.Kind == TokenKind.String) { s.Next(); return new LiteralString(tok.Text); }

        if (tok.Kind == TokenKind.Ident)
        {
            switch (tok.Text)
            {
                case "true": s.Next(); return new LiteralBool(true);
                case "false": s.Next(); return new LiteralBool(false);
                case "resource":
                    s.Next(); s.Expect(TokenKind.LBracket);
                    var field = s.Expect(TokenKind.String).Text;
                    s.Expect(TokenKind.RBracket);
                    return new AttributeRef(field);
                case "context":
                    s.Next(); s.Expect(TokenKind.Dot);
                    var which = s.Expect(TokenKind.Ident).Text;
                    return which switch
                    {
                        "now" => new ContextNow(),
                        "subject" => new ContextSubject(),
                        _ => throw new DslParseException($"unknown context member '{which}'", tok.Line, tok.Column),
                    };
                case "hour":
                    s.Next(); s.Expect(TokenKind.LParen);
                    var ts = ParseCondExpr(s);
                    s.Expect(TokenKind.RParen);
                    return new HourOf(ts);
                default:
                    s.Next();
                    // name in (a, b, ...) => InList, else ParamRef
                    if (s.Accept(TokenKind.In))
                    {
                        s.Expect(TokenKind.LParen);
                        var items = new List<ConditionExpr> { ParseCondExpr(s) };
                        while (s.Accept(TokenKind.Comma)) items.Add(ParseCondExpr(s));
                        s.Expect(TokenKind.RParen);
                        return new InList(new ParamRef(tok.Text), items);
                    }
                    return new ParamRef(tok.Text);
            }
        }

        throw new DslParseException($"unexpected token '{tok.Text}' in condition body", tok.Line, tok.Column);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter ConditionParseTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the whole parse suite (relations + permissions + conditions)**

Run: `dotnet test tests/Custodex.Dsl.Tests`
Expected: PASS — the stub file is gone and every parser path is real.

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Dsl tests/Custodex.Dsl.Tests
git commit -m "feat: parse conditions and CEL-shaped bodies to the m0/06 AST"
```

---

### Task 5: `SchemaWriter` — serialize `Schema` back to DSL text

**Files:**
- Create: `src/Custodex.Dsl/SchemaWriter.cs`
- Test: `tests/Custodex.Dsl.Tests/SchemaWriterTests.cs`

**Interfaces:**
- Produces: `static class SchemaWriter` with `string Write(Schema schema)` rendering types, relations, permissions (with parenthesis-minimal but round-trip-faithful algebra), and conditions (params + body).
- Consumes: `Schema`/`PermExpr`/`ConditionExpr` AST; the `m0/06` body nodes.

> **Parenthesization rule (round-trip faithfulness).** Because the parser uses one left-associative tier for `+ & -`, the writer emits a child in parentheses **only** when it is a binary algebra node appearing on the **right** of another binary algebra node (right child of `Union`/`Intersect`/`Exclude` that is itself one of those). A left child never needs parentheses (left-association reparses it identically). `Arrow`, `RelationRef`, and `Conditioned`-wrapped primaries are atomic-enough that the `with` re-binds correctly. This guarantees `Parse(Write(schema)) == schema` for the algebra; the property test in Task 6 proves it for generated trees.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Dsl.Tests/SchemaWriterTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Dsl;
using Custodex.Dsl.Parsing;
using Shouldly;
using Xunit;

namespace Custodex.Dsl.Tests;

public class SchemaWriterTests
{
    [Fact]
    public void Writes_a_type_with_a_relation_and_permission()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("medicator", s => s.User().SubjectSet("group", "member"))
                .Relation("enclosure", s => s.Type("enclosure"))
                .Relation("blocked", s => s.User())
                .Permission("edit", p => p.Relation("medicator").Arrow("enclosure", "edit").Exclude(x => x.Relation("blocked"))))
            .Build();

        var text = SchemaWriter.Write(schema);

        text.ShouldContain("type animal");
        text.ShouldContain("relation medicator: user | group#member");
        text.ShouldContain("permission edit = medicator + enclosure->edit - blocked");
    }

    [Fact]
    public void Writes_wildcard_subject()
    {
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t.Relation("is_quarantine", s => s.Wildcard("user")))
            .Build();
        SchemaWriter.Write(schema).ShouldContain("relation is_quarantine: user:*");
    }

    [Fact]
    public void Parenthesizes_only_a_right_binary_child()
    {
        // Union(a, Exclude(b, c)) must render as "a + (b - c)" to round-trip.
        var schema = new Schema("v1",
            [new EntityTypeDef("t",
                [new RelationDef("a", [new SubjectTypeRef("user")]),
                 new RelationDef("b", [new SubjectTypeRef("user")]),
                 new RelationDef("c", [new SubjectTypeRef("user")])],
                [new PermissionDef("p", new Union(new RelationRef("a"),
                    new Exclude(new RelationRef("b"), new RelationRef("c"))))])],
            []);

        SchemaWriter.Write(schema).ShouldContain("permission p = a + (b - c)");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter SchemaWriterTests`
Expected: FAIL — `SchemaWriter` not defined.

- [ ] **Step 3: Implement `SchemaWriter`**

```csharp
// src/Custodex.Dsl/SchemaWriter.cs
using System.Globalization;
using System.Text;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

namespace Custodex.Dsl;

public static class SchemaWriter
{
    public static string Write(Schema schema)
    {
        var sb = new StringBuilder();
        foreach (var type in schema.Types)
        {
            sb.Append("type ").Append(type.Name).AppendLine(" {");
            foreach (var r in type.Relations)
                sb.Append("  relation ").Append(r.Name).Append(": ").AppendLine(WriteSubjects(r.AllowedSubjects));
            foreach (var p in type.Permissions)
                sb.Append("  permission ").Append(p.Name).Append(" = ").AppendLine(WritePerm(p.Expression));
            sb.AppendLine("}");
        }
        foreach (var c in schema.Conditions)
            sb.AppendLine(WriteCondition(c));
        return sb.ToString();
    }

    private static string WriteSubjects(IReadOnlyList<SubjectTypeRef> subjects) =>
        string.Join(" | ", subjects.Select(WriteSubject));

    private static string WriteSubject(SubjectTypeRef s) => s switch
    {
        { Wildcard: true } => $"{s.Type}:*",
        { Relation: not null } => $"{s.Type}#{s.Relation}",
        _ => s.Type,
    };

    private static bool IsBinaryAlgebra(PermExpr e) => e is Union or Intersect or Exclude;

    private static string WritePerm(PermExpr expr) => expr switch
    {
        RelationRef r => r.Relation,
        Arrow a => $"{a.Relation}->{a.Permission}",
        Conditioned c => $"{WritePermChild(c.Inner, parenthesizeBinary: true)} with {c.ConditionName}",
        Union u => $"{WritePerm(u.Left)} + {WritePermChild(u.Right, parenthesizeBinary: true)}",
        Intersect i => $"{WritePerm(i.Left)} & {WritePermChild(i.Right, parenthesizeBinary: true)}",
        Exclude x => $"{WritePerm(x.Left)} - {WritePermChild(x.Right, parenthesizeBinary: true)}",
        _ => throw new InvalidOperationException($"unknown PermExpr {expr.GetType().Name}"),
    };

    // A binary-algebra node on the RIGHT of another binary-algebra node must be parenthesized
    // so left-association reparses it identically.
    private static string WritePermChild(PermExpr child, bool parenthesizeBinary) =>
        parenthesizeBinary && IsBinaryAlgebra(child) ? $"({WritePerm(child)})" : WritePerm(child);

    private static string WriteCondition(ConditionDef c)
    {
        var @params = string.Join(", ", c.Parameters.Select(p => $"{p.Name}: {WriteType(p.Type)}"));
        var head = $"condition {c.Name}({@params})";
        return c.Body is EmptyConditionBody ? head : $"{head} = {WriteBody(c.Body)}";
    }

    private static string WriteType(ConditionType t) => t switch
    {
        ConditionType.Bool => "bool", ConditionType.Int => "int", ConditionType.Long => "long",
        ConditionType.Double => "double", ConditionType.String => "string", ConditionType.Timestamp => "timestamp",
        _ => throw new InvalidOperationException($"unknown ConditionType {t}"),
    };

    private static string WriteBody(ConditionExpr e) => e switch
    {
        LiteralBool b => b.Value ? "true" : "false",
        LiteralInt i => i.Value.ToString(CultureInfo.InvariantCulture),
        LiteralDouble d => d.Value.ToString("0.0###############", CultureInfo.InvariantCulture),
        LiteralString str => $"\"{str.Value}\"",
        ParamRef p => p.Name,
        AttributeRef a => $"resource[\"{a.Field}\"]",
        ContextNow => "context.now",
        ContextSubject => "context.subject",
        HourOf h => $"hour({WriteBody(h.Timestamp)})",
        Not n => $"!{Group(n.Inner)}",
        Compare c => $"{Group(c.Left)} {CompareSym(c.Op)} {Group(c.Right)}",
        BoolOp bo => $"{Group(bo.Left)} {(bo.Op == BoolConnective.And ? "&&" : "||")} {Group(bo.Right)}",
        Arithmetic ar => $"{Group(ar.Left)} {ArithSym(ar.Op)} {Group(ar.Right)}",
        InList il => $"{WriteBody(il.Item)} in ({string.Join(", ", il.Items.Select(WriteBody))})",
        _ => throw new InvalidOperationException($"unknown ConditionExpr {e.GetType().Name}"),
    };

    // Parenthesize any compound operand so condition precedence round-trips unambiguously.
    private static string Group(ConditionExpr e) => e is Compare or BoolOp or Arithmetic or Not
        ? $"({WriteBody(e)})" : WriteBody(e);

    private static string CompareSym(CompareOp op) => op switch
    {
        CompareOp.Eq => "==", CompareOp.Ne => "!=", CompareOp.Lt => "<",
        CompareOp.Le => "<=", CompareOp.Gt => ">", CompareOp.Ge => ">=",
        _ => throw new InvalidOperationException(),
    };

    private static string ArithSym(ArithOp op) => op switch
    {
        ArithOp.Add => "+", ArithOp.Sub => "-", ArithOp.Mul => "*", ArithOp.Div => "/",
        _ => throw new InvalidOperationException(),
    };
}
```

> **Why `Group` parenthesizes liberally in condition bodies.** The writer wraps every compound condition operand in parentheses. This is round-trip-safe (the parser drops redundant parens via `"(" condExpr ")"`) and avoids any precedence ambiguity at the cost of a few extra parens. The property test (Task 6) checks `Parse(Write(x)) == x`, not minimal text, so liberal grouping is correct.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter SchemaWriterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Dsl tests/Custodex.Dsl.Tests
git commit -m "feat: add SchemaWriter serializing Schema back to DSL text"
```

---

### Task 6: Round-trip property + animal-schema parity with the builder

**Files:**
- Create: `tests/Custodex.Dsl.Tests/RoundTripTests.cs`
- Create: `tests/Custodex.Dsl.Tests/SchemaGenerators.cs`
- Test: `tests/Custodex.Dsl.Tests/AnimalSchemaParityTests.cs`

**Interfaces:**
- Produces: a CsCheck `Gen<Schema>` over relations, permission algebra trees (`Union`/`Intersect`/`Exclude`/`Arrow`/`RelationRef`/`Conditioned`), and condition bodies; the property `Parse(Write(schema)) == schema`; and a parity test that the `m0/02` animal schema text parses to the **same** AST the `SchemaBuilder` produces.
- Consumes: `SchemaParser`, `SchemaWriter`, CsCheck `Gen`, `SchemaBuilder`.

> **The two normative properties of this plan:**
> 1. `Parse(Write(schema)) == schema` for generated schemas (records give structural equality over the whole AST).
> 2. The animal schema authored as DSL text parses to the identical `Schema` the `m0/02` builder produces — proving the DSL is a faithful second authoring surface for the same canonical model (spec §5.5).

- [ ] **Step 1: Write the generators**

```csharp
// tests/Custodex.Dsl.Tests/SchemaGenerators.cs
using CsCheck;
using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Dsl.Tests;

public static class SchemaGenerators
{
    // A fixed relation pool so generated permission trees only reference declared relations.
    private static readonly string[] Relations = ["a", "b", "c", "blocked", "medicator"];
    private static readonly string[] ArrowRels = ["enclosure", "site", "category"];
    private static readonly string[] ArrowPerms = ["edit", "view", "manage"];

    public static readonly Gen<PermExpr> Leaf = Gen.OneOf(
        Gen.OneOfConst<PermExpr>(Relations.Select(r => (PermExpr)new RelationRef(r)).ToArray()),
        Gen.Select(Gen.OneOfConst(ArrowRels), Gen.OneOfConst(ArrowPerms),
            (rel, perm) => (PermExpr)new Arrow(rel, perm)));

    public static Gen<PermExpr> PermExprGen(int depth) =>
        depth <= 0
            ? Leaf
            : Gen.Frequency(
                (3, Leaf),
                (1, Gen.Select(PermExprGen(depth - 1), PermExprGen(depth - 1), (l, r) => (PermExpr)new Union(l, r))),
                (1, Gen.Select(PermExprGen(depth - 1), PermExprGen(depth - 1), (l, r) => (PermExpr)new Intersect(l, r))),
                (1, Gen.Select(PermExprGen(depth - 1), PermExprGen(depth - 1), (l, r) => (PermExpr)new Exclude(l, r))),
                (1, Gen.Select(PermExprGen(depth - 1), Gen.OneOfConst("within_hours", "at_least"),
                    (inner, cond) => (PermExpr)new Conditioned(inner, cond))));

    public static readonly Gen<Schema> SchemaGen =
        PermExprGen(3).Array[1, 3].Select(perms =>
        {
            var relations = Relations.Select(r => new RelationDef(r, [new SubjectTypeRef("user")])).ToList();
            var permissions = perms.Select((e, idx) => new PermissionDef($"perm{idx}", e)).ToList();
            var type = new EntityTypeDef("t", relations, permissions);
            // a condition with a real body so the body round-trip is exercised too
            ConditionExpr body = new BoolOp(
                new Compare(new HourOf(new ContextNow()), CompareOp.Ge, new ParamRef("start")),
                BoolConnective.And,
                new Compare(new AttributeRef("weight"), CompareOp.Lt, new ParamRef("end")));
            var condition = new ConditionDef("within_hours",
                [new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int)], body);
            return new Schema("v1", [type], [condition]);
        });
}
```

> **`Gen.OneOfConst`/`Gen.Frequency` are CsCheck combinators.** If a combinator name differs in the pinned CsCheck version, swap to the equivalent (`Gen.OneOf` with `Gen.Const`, `Gen.Frequency` with weighted tuples). The generator's intent — random algebra trees over a fixed relation pool plus one real condition body — is what matters.

- [ ] **Step 2: Write the round-trip property test**

```csharp
// tests/Custodex.Dsl.Tests/RoundTripTests.cs
using CsCheck;
using Custodex.Dsl;
using Custodex.Dsl.Parsing;
using Shouldly;
using Xunit;

namespace Custodex.Dsl.Tests;

public class RoundTripTests
{
    [Fact]
    public void Parse_of_write_equals_original_for_generated_schemas()
    {
        SchemaGenerators.SchemaGen.Sample(schema =>
        {
            var text = SchemaWriter.Write(schema);
            var reparsed = SchemaParser.Parse(text);
            return reparsed == schema;   // record structural equality over the whole AST
        });
    }

    [Fact]
    public void Write_of_parse_is_stable_for_a_fixed_text()
    {
        const string src = """
            type animal {
              relation medicator: user | group#member
              relation enclosure: enclosure
              relation blocked: user
              permission edit = medicator + enclosure->edit - blocked
            }
            """;
        var once = SchemaWriter.Write(SchemaParser.Parse(src));
        var twice = SchemaWriter.Write(SchemaParser.Parse(once));
        twice.ShouldBe(once);   // writer output is a fixed point under reparse
    }
}
```

- [ ] **Step 3: Write the animal-schema parity test**

```csharp
// tests/Custodex.Dsl.Tests/AnimalSchemaParityTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Dsl.Parsing;
using Shouldly;
using Xunit;

namespace Custodex.Dsl.Tests;

public class AnimalSchemaParityTests
{
    [Fact]
    public void Animal_dsl_parses_to_the_same_AST_the_builder_produces()
    {
        // The m0/02 animal schema, authored as DSL text.
        const string dsl = """
            type group {
              relation member: user | group#member
            }
            type animal {
              relation medicator: user | group#member
              relation enclosure: enclosure
              relation blocked: user | group#member
              permission edit = medicator + enclosure->edit - blocked
            }
            condition within_hours(start: int, end: int)
            """;

        var fromDsl = SchemaParser.Parse(dsl);

        // The exact m0/02 builder schema (params-only condition => EmptyConditionBody, matching the DSL).
        var fromBuilder = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("animal", t => t
                .Relation("medicator", s => s.User().SubjectSet("group", "member"))
                .Relation("enclosure", s => s.Type("enclosure"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("medicator").Arrow("enclosure", "edit").Exclude(x => x.Relation("blocked"))))
            .Condition("within_hours", c => c.Int("start").Int("end"))
            .Build();

        fromDsl.ShouldBe(fromBuilder);   // identical canonical Schema
    }
}
```

- [ ] **Step 4: Run to verify (expect PASS)**

Run: `dotnet test tests/Custodex.Dsl.Tests --filter "RoundTripTests|AnimalSchemaParityTests"`
Expected: PASS. A CsCheck counterexample on the round-trip is a genuine parser/writer bug — minimize it and fix the parenthesization rule. A parity failure means the parser's tree shape diverges from the builder's; fix the parser, not the test.

- [ ] **Step 5: Run the whole DSL suite**

Run: `dotnet test tests/Custodex.Dsl.Tests`
Expected: PASS (lexer, relation, permission, condition, writer, round-trip, parity).

- [ ] **Step 6: Commit**

```bash
git add tests/Custodex.Dsl.Tests
git commit -m "test: prove DSL round-trip property and animal-schema builder parity"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Operators map exactly: `+`→`Union`, `&`→`Intersect`, `-`→`Exclude`, `rel->perm`→`Arrow`, `expr with cond`→`Conditioned`, subject `type:*`→wildcard `SubjectTypeRef`, `type#rel`→subject-set.
- [ ] The permission algebra parses left-associatively over one tier so `medicator + enclosure->edit - blocked` yields `Exclude(Union(RelationRef, Arrow), RelationRef)` — the same tree the `m0/02` builder produces.
- [ ] Condition bodies parse to the `m0/06` AST (`Compare`/`BoolOp`/`HourOf`/`ParamRef`/`AttributeRef`/`ContextNow`/`ContextSubject`/literals); a param-only condition attaches `EmptyConditionBody`.
- [ ] `SchemaWriter` parenthesizes a right binary-algebra child (and any compound condition operand) so it round-trips.
- [ ] `Parse(Write(schema)) == schema` holds for generated schemas (CsCheck); the animal schema DSL text equals the builder's `Schema`.

## Contract gaps (reported, not changed)

- **`EmptyConditionBody` location.** The DSL parser and writer reference `Custodex.Core.EmptyConditionBody` (the placeholder record `m0/02` declares in `Custodex.Core`'s `SchemaBuilder.cs`) for param-only conditions. This couples `Custodex.Dsl` to a type that lives in `Custodex.Core` rather than `Custodex.Abstractions`. `Custodex.Dsl` already references `Custodex.Core` (for the `m0/06` body nodes, which also live in `Custodex.Core.Conditions`), so there is no new dependency, but if the contract owner prefers, `EmptyConditionBody` could move to `Custodex.Abstractions` alongside the `ConditionExpr` marker. Flagged; not changed.
- **No explicit schema-version syntax.** The DSL has no surface for the `Schema.Version` string (it is `"v1"` by default via `SchemaParser.Parse(source)`, or caller-supplied via `Parse(source, version)`). The round-trip property fixes the version on both sides, so it holds; if the DSL should carry a version header (e.g. a leading `version "v2"` line), that is an additive grammar change. Flagged as a possible future addition; the current contract has no version-in-text requirement.
- **Condition body node home (`m0/06`).** The body nodes (`Compare`, `HourOf`, etc.) live in `Custodex.Core.Conditions` per `m0/06`. This plan's parser/writer depend on that namespace. If `m0/06`'s final node names or namespace differ, adjust the `using` and the node constructors here; the grammar and tests are unaffected. No contract change requested.
