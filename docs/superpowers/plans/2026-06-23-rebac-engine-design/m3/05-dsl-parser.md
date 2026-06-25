# M3/05 — DSL Parser & Serializer

**Goal:** A text DSL ⇄ canonical `Schema` round-trip. A Permify/OpenFGA-style schema text parses into the **exact** `Custodex.Abstractions` `Schema`/`PermExpr` AST (`Union`=`+`, `Intersect`=`&`, `Exclude`=`-`, `Arrow`=`rel->perm`, `Conditioned`=`expr with cond`), and a serializer renders any `Schema` back to that text. The property `parse(serialize(schema)) == schema` holds for generated schemas, and the animal schema parses to the **same AST the `SchemaBuilder` produces** — proving the DSL is a faithful second authoring surface for the one canonical model (spec §5.5).

**For implementers:** drive this plan with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each task is TDD — Red → Green → Commit — tracked by its `- [ ]` checkbox. One Conventional Commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture:** the DSL lives **in `Custodex.Core`** under namespace `Custodex.Core.Dsl` (with `.Lexing`/`.Parsing` sub-namespaces), tested in `Custodex.Core.Tests` (see `../README.md` → Post-dispatch contract reconciliations — there is no separate `Custodex.Dsl` project). A hand-written recursive-descent parser in three layers: a `Lexer` (identifiers, the operators `+ & - -> | * : # ( ) = ,`, the keywords `type relation permission condition with in`, and condition operators), a `SchemaParser` producing `Schema`, and a `SchemaWriter` rendering `Schema` to text. The permission grammar fixes precedence so the parse tree matches the builder's left-associative accumulation: `+ & -` share one left-associative tier, `Arrow` and parenthesized groups are primaries, and `with` (Conditioned) binds tightest to its left operand. The serializer is the inverse — parenthesis-minimal but round-trip-faithful.

**Tech stack:** .NET 10 / C# 14, xUnit + Shouldly, **CsCheck** (for the round-trip property).

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies (see README):** the `Schema`/`PermExpr` AST and `SchemaBuilder` (for the animal-schema parity test); the concrete `ConditionExpr` body nodes (`LiteralInt`, `ParamRef`, `AttributeRef`, `ContextNow`, `Compare`, `BoolOp`, `HourOf`, etc., in `Custodex.Core.Conditions`) and the `EmptyConditionBody` placeholder — all already in `Custodex.Core`, so the DSL adds no new cross-assembly dependency.

## DSL grammar (normative)

Line-oriented within blocks; whitespace is insignificant except as a token separator. `//` begins a line comment.

```
schema      := (typeDecl | conditionDecl)*

typeDecl    := "type" ident "{" member* "}"
member      := relationDecl | permissionDecl
relationDecl   := "relation" ident ":" subjectList
subjectList    := subject ("|" subject)*
subject     := ident                       // any instance of a type, e.g. user   -> SubjectTypeRef(type, null, false)
             | ident ":" "*"               // wildcard, e.g. user:*               -> SubjectTypeRef(type, null, true)
             | ident "#" ident             // subject-set, e.g. group#member      -> SubjectTypeRef(type, relation, false)

permissionDecl := "permission" ident "=" permExpr

// Permission algebra. One left-associative tier for + & - so the tree matches the
// SchemaBuilder accumulation: terms combine left-to-right by their operator.
permExpr    := withExpr (("+" | "&" | "-") withExpr)*
withExpr    := primary ("with" ident)*     // Conditioned wraps its left operand
primary     := ident                       // RelationRef(ident)  (a relation OR a nested permission name)
             | ident "->" ident            // Arrow(relation, permission)
             | "(" permExpr ")"

conditionDecl  := "condition" ident "(" paramList? ")" ("=" condExpr)?
paramList   := param ("," param)*
param       := ident ":" condType
condType    := "bool" | "int" | "long" | "double" | "string" | "timestamp"

// Condition body (CEL-shaped). Standard precedence: || < && < comparison < +/- < */ < unary < primary.
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

**Algebra-tier precedence is deliberate.** `+ & -` share one left-associative tier, so `a + b - c` parses as `Exclude(Union(a, b), c)` — exactly the tree the builder's "default-chain then wrap" accumulation produces. A consumer who needs `a + (b - c)` writes the parentheses; the serializer emits them only when the tree requires them.

---

### Task 1: Create the lexer and token model

- [ ] **Files:** add `src/Custodex.Core/Dsl/Lexing/Token.cs`, `Dsl/Lexing/Lexer.cs`, `Dsl/DslParseException.cs`; test `tests/Custodex.Core.Tests/Dsl/LexerTests.cs`.

**Produces:** `enum TokenKind`; `readonly record struct Token(TokenKind Kind, string Text, int Line, int Column)`; `Lexer.Tokenize(string) -> IReadOnlyList<Token>` (terminated by an `Eof` token); `DslParseException(message, line, column)` exposing `Line`/`Column`.
**Consumes (see README):** nothing beyond the BCL.

**Behavior:** the lexer scans identifiers/keywords, number and string literals, and the operator set, tracking line/column. Two-char operators (`->`, `&&`, `||`, `==`, `!=`, `<=`, `>=`) are matched before their single-char prefixes. `//` runs to end of line and is skipped. An unterminated string or an unexpected character throws `DslParseException` carrying the line/column. The condition-body operators (`! && || == != < <= > >= * /`) share the token set so Tasks 3–4 reuse it.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `relation member: user \| group#member \| user:*` | yields `Relation`, `Pipe`, `Hash`, `Star`; ends with `Eof` |
| `medicator + enclosure->edit - blocked & vet` | yields `Plus`, `Arrow`, `Minus`, `Amp` |
| a `// comment` inside a block | no token carries the comment text |
| an unexpected `$` on line 2 | `DslParseException` with `Line == 2` |

**Done when:** build clean; cases pass.

---

### Task 2: Parse types, relations, and subject lists

- [ ] **Files:** add `Dsl/Parsing/TokenStream.cs`, `Dsl/Parsing/SchemaParser.cs`; test `tests/Custodex.Core.Tests/Dsl/RelationParseTests.cs`.

**Produces:** `TokenStream` (a cursor with `Peek`/`Next`/`Accept`/`Expect`); `SchemaParser.Parse(string) -> Schema` and a `Parse(string, version)` overload. This task implements `type`/`relation` parsing; permissions/conditions follow in Tasks 3–4.
**Consumes (see README):** the lexer; `Schema`, `EntityTypeDef`, `RelationDef`, `SubjectTypeRef`.

**Behavior:** `SchemaParser` is a `partial` static class so each task adds its members in its own file. The top level dispatches on `type` / `condition`; a type body dispatches on `relation` / `permission`. A subject is `ident` (any instance), `ident:*` (wildcard `SubjectTypeRef`), or `ident#ident` (subject-set). The version defaults to `"v1"` when not caller-supplied. For this task, `ParsePermission`/`ParseCondition` are temporary stubs that throw (replaced by Tasks 3–4) so the parser dispatch compiles whole.

**Cases to pin:**

| Setup | Expect |
|---|---|
| a `group` type with `user \| group#member` and an `enclosure` with `user:*` | the `SubjectTypeRef`s parse, including the subject-set and wildcard |
| empty source | no types |
| `type x { }` | version `"v1"` |

**Done when:** build clean; cases pass.

---

### Task 3: Parse the permission algebra to the exact AST

- [ ] **Files:** add `Dsl/Parsing/SchemaParser.Permission.cs`; drop the permission stub; test `tests/Custodex.Core.Tests/Dsl/PermissionParseTests.cs`.

**Produces:** `ParsePermission` building `RelationRef`/`Union`/`Intersect`/`Exclude`/`Arrow`/`Conditioned` from the operators `+ & - -> with`.
**Consumes (see README):** `PermExpr` and its subtypes.

**Behavior:** the parse tree must match the `SchemaBuilder` output. For `medicator + enclosure->edit - blocked` the result is `Exclude(Union(RelationRef "medicator", Arrow("enclosure","edit")), RelationRef "blocked")` — produced by the single left-associative tier for `+ & -`. `with` binds to its immediate left primary (Conditioned wraps the tightest operand); parentheses around a `permExpr` override left-association; a bare `ident` is `RelationRef`, `ident->ident` is `Arrow`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `medicator + enclosure->edit - blocked` | `Exclude(Union(RelationRef, Arrow), RelationRef)` |
| `a & b` | `Intersect(RelationRef a, RelationRef b)` |
| `a + (b - blocked)` | `Union(RelationRef a, Exclude(RelationRef b, RelationRef blocked))` |
| `a with within_hours` | `Conditioned(RelationRef a, "within_hours")` |

**Done when:** build clean; cases pass; the algebra tree matches the builder's.

---

### Task 4: Parse conditions and their CEL-shaped bodies

- [ ] **Files:** add `Dsl/Parsing/SchemaParser.Condition.cs`; delete the remaining stub file; test `tests/Custodex.Core.Tests/Dsl/ConditionParseTests.cs`.

**Produces:** `ParseCondition -> ConditionDef`, parsing the param list (`name: type`) and an optional `= body` whose AST uses the condition body nodes; a param-only condition with no body attaches `EmptyConditionBody`.
**Consumes (see README):** `ConditionDef`, `ConditionParam`, `ConditionType`, `ConditionExpr`; the body nodes + `EmptyConditionBody` (`Custodex.Core.Conditions`).

**Behavior:** the body parses with standard precedence (`|| < && < comparison < +/- < */ < unary < primary`). Primaries map to nodes: number → `LiteralInt`/`LiteralDouble` (a `.` makes it double), string → `LiteralString`, `true`/`false` → `LiteralBool`, `resource["field"]` → `AttributeRef`, `context.now`/`context.subject` → `ContextNow`/`ContextSubject`, `hour(expr)` → `HourOf`, `ident in (…)` → `InList` (item is the leading ident), any other `ident` → `ParamRef`. Param types map by name to `ConditionType`; an unknown type name throws `DslParseException`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `within_hours(start: int, end: int) = hour(context.now) >= start && hour(context.now) < end` | `BoolOp(And, Compare(Ge, HourOf(ContextNow), ParamRef start), …)` |
| `at_least(n: int) = resource["weight"] >= n` | `Compare(AttributeRef "weight", Ge, ParamRef "n")` |
| `is_creator() = resource["created_by"] == context.subject` | `Compare(AttributeRef, Eq, ContextSubject)` |
| `legacy(n: int)` (no body) | `EmptyConditionBody` |

**Done when:** build clean; cases pass; the whole parse suite (relations + permissions + conditions) is green with no stubs.

---

### Task 5: `SchemaWriter` — serialize `Schema` back to DSL text

- [ ] **Files:** add `src/Custodex.Core/Dsl/SchemaWriter.cs`; test `tests/Custodex.Core.Tests/Dsl/SchemaWriterTests.cs`.

**Produces:** `static class SchemaWriter` with `Write(Schema) -> string` rendering types, relations, permissions (parenthesis-minimal but round-trip-faithful), and conditions (params + body).
**Consumes (see README):** the `Schema`/`PermExpr`/`ConditionExpr` AST and the body nodes.

**Behavior:**
- **Permission parenthesization.** Because the parser uses one left-associative tier for `+ & -`, the writer parenthesizes a child **only** when it is a binary-algebra node (`Union`/`Intersect`/`Exclude`) appearing on the **right** of another binary-algebra node — left-association reparses a left child identically. So `Union(a, Exclude(b, c))` renders as `a + (b - c)`. This guarantees `Parse(Write(schema)) == schema` for the algebra; Task 6 proves it for generated trees.
- **Condition bodies** wrap every compound operand (`Compare`/`BoolOp`/`Arithmetic`/`Not`) in parentheses. This is round-trip-safe (the parser drops redundant parens) and sidesteps precedence ambiguity; the property test checks structural equality, not minimal text, so liberal grouping is correct.
- Subjects render as `type`, `type#relation`, or `type:*`; a param-only condition renders head-only (no `= body`).

**Cases to pin:**

| Setup | Expect |
|---|---|
| animal type with `medicator + enclosure->edit - blocked` | text contains `relation medicator: user \| group#member` and `permission edit = medicator + enclosure->edit - blocked` |
| a wildcard subject | text contains `user:*` |
| `Union(a, Exclude(b, c))` | text contains `a + (b - c)` |

**Done when:** build clean; cases pass.

---

### Task 6: Round-trip property + animal-schema parity with the builder

- [ ] **Files:** add `tests/Custodex.Core.Tests/Dsl/SchemaGenerators.cs`, `Dsl/RoundTripTests.cs`, `Dsl/AnimalSchemaParityTests.cs`.

**Produces:** a CsCheck `Gen<Schema>` over relations, permission algebra trees, and a real condition body; the property `Parse(Write(schema)) == schema`; and a parity test that the animal schema text parses to the same `Schema` the `SchemaBuilder` produces.
**Consumes (see README):** `SchemaParser`, `SchemaWriter`, CsCheck, `SchemaBuilder`.

**Behavior:** the two normative properties of this plan —
1. `Parse(Write(schema)) == schema` for generated schemas (records give structural equality over the whole AST). A CsCheck counterexample is a genuine parser/writer bug — minimize it and fix the parenthesization rule, not the test.
2. The animal schema authored as DSL text parses to the identical `Schema` the builder produces (a param-only `within_hours` condition ⇒ `EmptyConditionBody` on both sides). A parity failure means the parser's tree shape diverges from the builder's — fix the parser.

The generator builds random algebra trees over a fixed relation pool plus one real condition body; also assert that `Write` is a fixed point under reparse for a known text. (Combinator names track the pinned CsCheck version; the generator intent is what matters.)

**Cases to pin:**

| Setup | Expect |
|---|---|
| generated schemas | `Parse(Write(schema)) == schema` |
| a fixed animal text | `Write(Parse(text))` is stable on reparse |
| animal DSL text vs the builder schema | structurally equal |

**Done when:** build clean; the property holds; the animal DSL equals the builder's `Schema`; the whole DSL suite is green.

---

## Self-review checklist

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [ ] Operators map exactly: `+`→`Union`, `&`→`Intersect`, `-`→`Exclude`, `rel->perm`→`Arrow`, `expr with cond`→`Conditioned`, `type:*`→wildcard, `type#rel`→subject-set.
- [ ] The permission algebra parses left-associatively over one tier so `medicator + enclosure->edit - blocked` yields `Exclude(Union(RelationRef, Arrow), RelationRef)` — the builder's tree.
- [ ] Condition bodies parse to the condition AST; a param-only condition attaches `EmptyConditionBody`.
- [ ] `SchemaWriter` parenthesizes a right binary-algebra child (and any compound condition operand) so it round-trips.
- [ ] `Parse(Write(schema)) == schema` holds for generated schemas; the animal DSL text equals the builder's `Schema`.
