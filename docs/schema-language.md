# Custodex Schema Language

A Custodex **schema** is the permission model for one store: the entity types, the relations objects of those types can have to subjects, the permissions computed over those relations, and the named conditions permissions may be gated on. The engine carries zero domain concepts — the schema is where a consuming application expresses its own.

A schema can be authored two equivalent ways, and both produce the same canonical `Schema` AST that the engine evaluates:

- **DSL text** — a compact textual language parsed by `SchemaParser.Parse` and serialized back by `SchemaWriter.Write` (the two round-trip).
- **`SchemaBuilder`** — a fluent C# API for building the same AST in code.

This page is the reference for both. Each construct is shown in DSL form first, then its `SchemaBuilder` equivalent.

## Relationship to Zanzibar / SpiceDB

Custodex is modelled on Google Zanzibar (the lineage behind SpiceDB and OpenFGA). If you know that family, most of this language is familiar, with two things worth calling out up front:

| Zanzibar / SpiceDB concept | Custodex |
| --- | --- |
| Relation with allowed subject types | `relation name: user \| group#member \| user:*` |
| `this` / direct relation in a rewrite | a bare relation name in a permission |
| `union` rewrite | `+` |
| `intersection` rewrite | `&` |
| `exclusion` rewrite | `-` |
| `tuple_to_userset` (arrow) | `relation->permission` |
| userset / subject set | `type:id#relation` subjects |
| wildcard (`*`) | `type:*` |
| caveats (CEL) | named `condition`s gated with `with` |

Two deliberate divergences from SpiceDB's surface:

1. **Operator precedence is flat.** `+`, `&`, and `-` share a single precedence level and associate left to right. `a + b & c` parses as `(a + b) & c`, **not** `a + (b & c)`. Group with parentheses when you mean otherwise.
2. **Conditions are first-class top-level declarations** with a typed parameter list and a small, fixed expression language (comparisons, boolean and arithmetic operators, `in`, `hour`, attribute and context reads) rather than inline CEL.

The two evaluation paths (the engine-driven walk and the SQL recursive CTEs) implement identical semantics for every construct here, and a differential property harness asserts they agree.

## A complete example

The same folder/document model in both forms. A document is editable by its owner or editors, by anyone who can edit its parent folder (inherited across the object graph), unless explicitly blocked.

**DSL**

```
type group {
    relation member: user | group#member
}

type folder {
    relation editor: user | group#member
    permission edit = editor
}

type document {
    relation parent: folder
    relation owner: user
    relation editor: user
    relation blocked: user
    permission edit = owner + editor + parent->edit - blocked
}
```

**`SchemaBuilder`**

```csharp
var schema = new SchemaBuilder("v1")
    .Type("group", t => t
        .Relation("member", s => s.User().SubjectSet("group", "member")))
    .Type("folder", t => t
        .Relation("editor", s => s.User().SubjectSet("group", "member"))
        .Permission("edit", p => p.Relation("editor")))
    .Type("document", t => t
        .Relation("parent", s => s.Type("folder"))
        .Relation("owner", s => s.User())
        .Relation("editor", s => s.User())
        .Relation("blocked", s => s.User())
        .Permission("edit", p => p
            .Relation("owner")
            .Union(u => u.Relation("editor"))
            .Union(u => u.Arrow("parent", "edit"))
            .Exclude(x => x.Relation("blocked"))))
    .Build();
```

Both produce the identical `Schema`. The DSL version string defaults to `v1`; pass a second argument to `SchemaParser.Parse(source, version)` to set it, or the constructor argument to `SchemaBuilder`.

## Lexical structure

- **Identifiers** start with a letter or `_` and continue with letters, digits, or `_`. Entity types, relations, permissions, condition names, and parameter names are all identifiers, compared **ordinal** (case-sensitive).
- **Keywords**: `type`, `relation`, `permission`, `condition`, `with`, `in`. These cannot be used as identifiers.
- **Whitespace** (spaces, tabs, newlines) separates tokens and is otherwise insignificant.
- **Comments**: `//` begins a line comment that runs to end of line.
- **String literals** are double-quoted and single-line: `"created_by"`. They appear only inside condition expressions.
- **Number literals** are integers (`42`) or, with a fractional part, doubles (`3.14`). They appear only inside condition expressions.
- The id `*` is reserved as the type-wide **wildcard** and is written only in the `type:*` subject shape.

## Types

A type groups the relations and permissions declared on objects of that type.

```
type document {
    // relations and permissions go here
}
```

```csharp
.Type("document", t => { /* relations and permissions */ })
```

A type body contains any number of `relation` and `permission` declarations, in any order. Relation and permission names must each be unique within the type.

## Relations

A relation is a stored edge: the `(object, relation, subject)` tuples the engine reads. A relation declares which **subject shapes** a tuple on it may name, separated by `|`.

```
relation editor: user | group#member | user:*
```

```csharp
.Relation("editor", s => s
    .User()                          // or .Type("user")
    .SubjectSet("group", "member")
    .Wildcard("user"))
```

There are three subject shapes:

| Shape | DSL | Builder | Meaning |
| --- | --- | --- | --- |
| **Type** | `user` | `.Type("user")` / `.User()` | Any instance of the type, named `user:alice`. |
| **Subject set** | `group#member` | `.SubjectSet("group", "member")` | Whoever holds `member` on a group, named `group:eng#member`. Resolved recursively, so group nesting is transitive. |
| **Wildcard** | `user:*` | `.Wildcard("user")` | Every instance of the type, granted by the single tuple naming `user:*`. |

`.User()` is shorthand for `.Type("user")`.

## Permissions

A permission is computed, never stored. Its expression composes relations into a decision.

```
permission edit = owner + editor + parent->edit - blocked
```

```csharp
.Permission("edit", p => p
    .Relation("owner")
    .Union(u => u.Relation("editor"))
    .Union(u => u.Arrow("parent", "edit"))
    .Exclude(x => x.Relation("blocked")))
```

### The algebra

| Operator | DSL | Builder | Grants when |
| --- | --- | --- | --- |
| Relation | `editor` | `.Relation("editor")` | the subject holds the relation `editor` on this object (directly, via `type:*`, or as a member of a subject set the relation grants to). |
| Union | `a + b` | `.Union(...)` (or chaining terms) | either side grants. |
| Intersection | `a & b` | `.Intersect(...)` | both sides grant. |
| Exclusion | `a - b` | `.Exclude(...)` | the left grants and the right does not. |
| Arrow | `rel->perm` | `.Arrow("rel", "perm")` | following `rel` reaches an object on which the subject holds `perm`. |
| Condition | `a with name` | `.Conditioned("name")` | `a` grants **and** the named condition passes. |
| Grouping | `( ... )` | nested `.Union(b => ...)` | — |

A bare identifier resolves a **relation or a permission** declared on the current type: a relation is resolved from its stored tuples, and a permission is evaluated as its own expression — so permissions layer on one another (`view = edit`, where `edit` is itself a permission). When a name is declared as both, the relation is used. To require a permission on a *related* object, use an arrow.

In the builder, chaining terms unions them: `p.Relation("owner").Relation("editor")` is `owner + editor`. `.Union(b => ...)` adds a grouped sub-expression as a further union term, while `.Intersect`, `.Exclude`, and `.Conditioned` fold the expression built so far against a new operand.

### Precedence and associativity

`+`, `&`, and `-` share **one** precedence level and are **left-associative**:

```
editor + container->update - blocked
```

parses as `(editor + (container->update)) - blocked`. Use parentheses to regroup:

```
owner + (editor - blocked)
```

`with` binds tighter than `+ & -`, attaching to its immediate left operand, and it chains:

```
owner + editor with within_window
```

is `owner + (editor with within_window)` — the condition gates only `editor`. The arrow `->` is part of a primary and binds tightest of all; an arrow is a single hop, and multi-level inheritance composes by the permission it targets itself using an arrow (see below).

### Inheritance of permissions

Permissions are derived at query time from the stored relations — no derived edge is ever materialized. Three patterns cover the inheritance shapes:

**Same object (computed userset)** — "an editor is also a viewer", and permissions may layer on other permissions of the same type:

```
permission comment = commenter + editor
permission view    = viewer + comment
```

**Across the object graph (tuple-to-userset / arrow)** — "a folder's editor can edit its documents". The arrow follows `parent` to each folder and requires `edit` there; because `folder.edit` may itself arrow to *its* parent, this composes to any depth:

```
type folder {
    relation editor: user
    relation parent: folder
    permission edit = editor + parent->edit
}
```

**Group nesting (subject sets)** — a relation that accepts `group#member` grants to every member of the group, including members of nested groups, resolved transitively.

## Conditions (ABAC)

A condition is a named, typed predicate declared at the top level (outside any type). Permissions reference it with `with`.

```
condition within_window(start: int, end: int) =
    hour(context.now) >= start && hour(context.now) < end
```

```csharp
.Condition("within_window",
    c => c.Int("start").Int("end"),
    b => b.And(
        b.Ge(b.Hour(b.Now()), b.Param("start")),
        b.Lt(b.Hour(b.Now()), b.Param("end"))))
```

A condition may also be declared with parameters and **no body**, deferring its predicate:

```
condition legacy(n: int)
```

```csharp
.Condition("legacy", c => c.Int("n"))
```

### Parameter types

| DSL | Builder | .NET |
| --- | --- | --- |
| `bool` | `.Bool(name)` | `bool` |
| `int` | `.Int(name)` | 32-bit integer |
| `long` | `.Long(name)` | 64-bit integer |
| `double` | `.Double(name)` | `double` |
| `string` | `.String(name)` | `string` |
| `timestamp` | `.Timestamp(name)` | an instant |

### Condition expression language

A condition body is a boolean expression. Precedence, lowest to highest:

1. `||` (boolean or)
2. `&&` (boolean and)
3. comparisons `==` `!=` `<` `<=` `>` `>=` — these do **not** chain (`a == b == c` is not valid; one comparison per operand)
4. `+` `-` (arithmetic)
5. `*` `/`
6. `!` (unary not)
7. primaries

The value sources available in a primary:

| DSL | Builder | Value |
| --- | --- | --- |
| `42`, `3.14`, `"text"`, `true`, `false` | `.Const(...)` | a literal |
| `start` | `.Param("start")` | a declared parameter |
| `resource["weight"]` | `.Attribute("weight")` | a field of the object's stored attributes |
| `timestamp(resource["expires"])` | `.Attribute("expires", ConditionType.Timestamp)` | a stored attribute read resolved as a timestamp, so a string-encoded instant compares chronologically rather than ordinally |
| `context.now` | `.Now()` | the request time (`RequestContext.Now`) |
| `context.subject` | `.Subject()` | the subject being checked |
| `hour(expr)` | `.Hour(expr)` | hour-of-day (0–23) of a timestamp |
| `n in (1, 2, 3)` | `.In(item, ...)` | membership test; the left operand is a parameter |
| `( expr )` | nested builder calls | grouping |

Time enters evaluation only through `context.now`; evaluation is otherwise deterministic. A condition that fails, or that reads a missing attribute, is **default-deny with a diagnostic — never an exception**.

A plain `resource["field"]` resolves by the stored value's runtime type, so two encodings of the same instant (say a string and a `DateTimeOffset`, or two differently-offset strings) compare ordinally and need not match. Wrapping the read in `timestamp(...)` declares the field a timestamp, so the value is parsed to an instant and compared chronologically:

```
condition not_expired() = context.now <= timestamp(resource["expires"])
```

```csharp
.Condition("not_expired", _ => { },
    b => b.Le(b.Now(), b.Attribute("expires", ConditionType.Timestamp)))
```

The cast applies only to an attribute read; both operands of a chronological comparison should be timestamps (`context.now`, a `timestamp` parameter, or another `timestamp(resource[...])`).

Gating a permission branch on a condition:

```
permission view = viewer with within_window
```

```csharp
.Permission("view", p => p.Relation("viewer").Conditioned("within_window"))
```

## Validation and round-trip

`SchemaValidator.Validate(schema)` checks a schema before it is activated: names resolve, types/relations/permissions/conditions are unique, arrows target a permission that exists on every type the followed relation can reach, condition bodies are well-typed, and no permission depends on itself through a non-terminating cycle. It returns every problem found, not just the first.

`SchemaWriter.Write(schema)` renders any `Schema` back to DSL text, and `SchemaParser.Parse` reads it back to an equal AST — so a schema built with `SchemaBuilder` and one parsed from text are interchangeable.

## Grammar

Schema grammar:

```
schema       := ( typeDecl | conditionDecl )*
typeDecl     := "type" ident "{" ( relationDecl | permissionDecl )* "}"
relationDecl := "relation" ident ":" subject ( "|" subject )*
subject      := ident                  // any instance of a type
              | ident "#" ident         // subject set
              | ident ":" "*"           // wildcard
permDecl     := "permission" ident "=" permExpr
permExpr     := withExpr ( ( "+" | "&" | "-" ) withExpr )*
withExpr     := primary ( "with" ident )*
primary      := "(" permExpr ")"
              | ident "->" ident         // arrow
              | ident                    // relation or same-type permission reference
```

Condition grammar:

```
conditionDecl := "condition" ident "(" paramList? ")" ( "=" condExpr )?
paramList     := param ( "," param )*
param         := ident ":" condType
condType      := "bool" | "int" | "long" | "double" | "string" | "timestamp"
condExpr      := orExpr
orExpr        := andExpr ( "||" andExpr )*
andExpr       := cmpExpr ( "&&" cmpExpr )*
cmpExpr       := addExpr ( ( "==" | "!=" | "<" | "<=" | ">" | ">=" ) addExpr )?
addExpr       := mulExpr ( ( "+" | "-" ) mulExpr )*
mulExpr       := unary ( ( "*" | "/" ) unary )*
unary         := "!" unary | condPrimary
condPrimary   := "(" condExpr ")"
              | number | string | "true" | "false"
              | "resource" "[" string "]"
              | "timestamp" "(" "resource" "[" string "]" ")"
              | "context" "." ( "now" | "subject" )
              | "hour" "(" condExpr ")"
              | ident "in" "(" condExpr ( "," condExpr )* ")"
              | ident                    // parameter reference
```
