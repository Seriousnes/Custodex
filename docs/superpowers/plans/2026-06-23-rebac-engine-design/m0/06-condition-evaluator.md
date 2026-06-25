# M0/06 — ABAC Condition Evaluator

**Goal:** The ABAC condition layer: concrete `ConditionExpr` body nodes (literals, parameter/attribute/context refs, comparison/boolean/arithmetic/`in`/date-time operators), a typed value model, a sandboxed static `ConditionEvaluator`, the `CelConditionEvaluator` adapter that plugs it into the authorizer's `IConditionEvaluator` seam, a `SchemaBuilder.Condition(name, params, body)` overload that attaches a real body, and a body type-checker wired into `SchemaValidator`.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** the body AST is a small expression tree under `ConditionExpr` (the abstract marker from m0/01), kept **CEL-shaped** so a future move to Google CEL needs no stored-schema change: literals, ref nodes (`param`, `resource[field]`, `context.now`/`context.subject`), and operator nodes. The static `ConditionEvaluator` walks it with a typed `CelValue` model over three input sources (attributes, request context, tuple parameters). Evaluation is deterministic and sandboxed — no I/O, no reflection, ambient time only via `RequestContext.Now`. A runtime failure (missing attribute, type mismatch) is **not** an escaping exception: it returns a `ConditionResult` carrying a diagnostic that the engine maps to deny (spec §10.3).

The authorizer (m0/05) depends on the `bool`-returning `IConditionEvaluator` seam, while the evaluator built here is a static class returning the richer `ConditionResult(bool Allowed, string? Diagnostic)`. `CelConditionEvaluator` bridges them: it implements `IConditionEvaluator`, forwards to `ConditionEvaluator.Evaluate`, maps `ConditionRef.Parameters → parameters`, and returns `ConditionResult.Allowed` (deny/error → false). It is the `IConditionEvaluator` implementation the authorizer uses once conditions are live.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly.

**Global Constraints:** see `../README.md` → Global Constraints (ambient time only through `RequestContext.Now`).

**Dependencies:** builds on m0/01 (`Custodex.Abstractions`, the `ConditionExpr` marker), m0/02 (`SchemaBuilder`, `EmptyConditionBody`), m0/03 (`ConditionParamChecker`, `SchemaValidator`), m0/05 (the `IConditionEvaluator` seam) — see README. Concrete nodes and the evaluator live in `Custodex.Core` (consistent with `EmptyConditionBody`).

---

### Task 1: Concrete `ConditionExpr` body nodes

- [ ] **Files:** create `src/Custodex.Core/Conditions/ConditionExprNodes.cs`. Test: `tests/Custodex.Core.Tests/Conditions/ConditionExprNodesTests.cs`.

**Produces** (all deriving from `Custodex.Abstractions.ConditionExpr`): literals `LiteralBool`, `LiteralInt(long)`, `LiteralDouble`, `LiteralString`; refs `ParamRef(Name)`, `AttributeRef(Field)`, `ContextNow`, `ContextSubject`; operators `Compare(Left, Op, Right)`, `BoolOp(Left, Op, Right)`, `Not(Inner)`, `Arithmetic(Left, Op, Right)`, `InList(Item, Items)`, `HourOf(Timestamp)`; enums `CompareOp { Eq, Ne, Lt, Le, Gt, Ge }`, `BoolConnective { And, Or }`, `ArithOp { Add, Sub, Mul, Div }`.
**Consumes (see README):** `ConditionExpr`.

**Behavior:** the node set is the whole condition algebra — a closed, pattern-matchable tree the evaluator and type-checker switch over. `AttributeRef` is `resource[field]`; `ContextNow`/`ContextSubject` are `context.now`/`context.subject`; `HourOf` is the date-time helper extracting hour-of-day.

**Cases to pin:**

| Setup | Expect |
|---|---|
| build `Compare(AttributeRef, Ge, ParamRef)` | pattern-matches; derives from `ConditionExpr` |
| build the `within_hours` body as a tree | top node is `BoolOp` with `And` |

**Done when:** build clean under TreatWarningsAsErrors; nodes pattern-match.

---

### Task 2: The value model and result

- [ ] **Files:** create `src/Custodex.Core/Conditions/CelValue.cs`, `…/ConditionResult.cs`. Test: `tests/Custodex.Core.Tests/Conditions/CelValueTests.cs`.

**Produces:** `CelValue` (typed wrapper with factories `Bool`/`Int(long)`/`Double`/`String`/`Timestamp(DateTimeOffset)`, a `CelKind Kind`, accessors `AsBool`/`AsLong`/`AsDouble`/`AsString`/`AsTimestamp`, and `IsNumeric`); `CelKind { Bool, Int, Double, String, Timestamp }`; `ConditionResult(bool Allowed, string? Diagnostic)` with static `Allow`, `Deny`, and `Error(string)`.
**Consumes (see README):** nothing beyond the BCL.

**Behavior:** `CelValue` centralizes numeric coercion (`Int` widens to `Double`). The evaluator never throws on a *data* problem — it produces `ConditionResult.Error`, a deny carrying a diagnostic.

**Cases to pin:**

| Setup | Expect |
|---|---|
| typed factories then accessors | round-trip |
| `Int(3).AsDouble()` | `3.0` (widening) |
| `ConditionResult.Error("…weight…")` | `Allowed` false; diagnostic carries the detail |

**Done when:** build clean; round-trip, widening, and error shape hold.

---

### Task 3: The sandboxed `ConditionEvaluator`

- [ ] **Files:** create `src/Custodex.Core/Conditions/ConditionEvaluator.cs`. Test: `tests/Custodex.Core.Tests/Conditions/ConditionEvaluatorTests.cs`.

**Produces:** `static ConditionResult ConditionEvaluator.Evaluate(ConditionDef definition, IReadOnlyDictionary<string, object?> attributes, RequestContext context, IReadOnlyDictionary<string, object?> parameters)`.
**Consumes (see README):** `ConditionDef`, `RequestContext`, `SubjectRef`; the body nodes and `CelValue`/`ConditionResult` (Tasks 1–2).

**Behavior:**
- Walks the body to a `CelValue`, requiring a top-level `Bool`. `ParamRef` reads `parameters[name]` typed by the declared `ConditionParam.Type`; `AttributeRef` reads the synced resource attributes; `ContextNow`/`ContextSubject` read request context; `HourOf` requires a timestamp operand and yields its hour as `Int`.
- Comparisons require comparable kinds (two numerics, two strings, two timestamps, two bools for `Eq`/`Ne`); arithmetic requires two numerics (division by zero is an error); `BoolOp` short-circuits; `InList` compares the item to each element by `Eq`. Any kind mismatch or missing reference returns `ConditionResult.Error` — never an escaping exception. `is_creator` compares `context.subject` (the bare subject **id**) against the `created_by` attribute.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `within_hours` with `now.hour` inside / outside the window | allow / deny |
| `at_least`: `resource[weight] >= n` | allow when ≥, deny when < |
| `is_creator`: `created_by == subject` | allow on match, deny otherwise |
| `at_least` with no `weight` attribute | deny with a diagnostic (not an exception) |
| `at_least` with a string `weight` vs an int | deny with a diagnostic |

**Done when:** build clean; the three spec conditions evaluate; missing-attribute/type-mismatch are deny-with-diagnostic.

---

### Task 4: The `CelConditionEvaluator` adapter

- [ ] **Files:** create `src/Custodex.Core/Conditions/CelConditionEvaluator.cs`. Test: `tests/Custodex.Core.Tests/Conditions/CelConditionEvaluatorTests.cs`.

**Produces:** `CelConditionEvaluator : IConditionEvaluator` — the seam implementation the authorizer uses for real conditions.
**Consumes (see README):** `IConditionEvaluator` (m0/05), the static `ConditionEvaluator` (Task 3), `ConditionDef`, `ConditionRef`, `RequestContext`.

**Behavior:** `Evaluate(def, invocation, resourceAttributes, context)` forwards to `ConditionEvaluator.Evaluate(def, resourceAttributes, context, invocation.Parameters)` and returns its `ConditionResult.Allowed` — mapping deny/error to false (default-deny per §10.3). This reconciles the authorizer's `bool` seam with the richer static evaluator without exposing diagnostics to the authorizer.

**Cases to pin:**

| Setup | Expect |
|---|---|
| adapter over a satisfied condition | true |
| adapter over a denied / errored condition | false |

**Done when:** build clean; the adapter forwards and maps results correctly.

---

### Task 5: `SchemaBuilder.Condition(name, params, body)` overload and the body builder

- [ ] **Files:** create `src/Custodex.Core/ConditionBodyBuilder.cs`; modify `src/Custodex.Core/SchemaBuilder.cs`. Test: `tests/Custodex.Core.Tests/ConditionBodyBuilderTests.cs`.

**Produces:** `ConditionBodyBuilder` — a CEL-shaped helper with `Param`/`Attribute`/`Now`/`Subject` refs, `Const(...)` literals, `Hour`, the comparisons `Eq`/`Ne`/`Lt`/`Le`/`Gt`/`Ge`, `And`/`Or`/`Not`, `Add`/`Sub`/`Mul`/`Div`, and `In(item, params items)`, each returning `ConditionExpr`; the `SchemaBuilder.Condition(string name, Action<ConditionParamBuilder> @params, Func<ConditionBodyBuilder, ConditionExpr> body)` overload.
**Consumes (see README):** `ConditionParamBuilder` (m0/02), the body nodes (Task 1), `ConditionDef`, `ConditionExpr`.

**Behavior:** the new overload declares a condition with a real body, attached alongside the params-only overload from m0/02 (which keeps producing `EmptyConditionBody` and whose test still passes).

**Cases to pin:**

| Setup | Expect |
|---|---|
| `Condition(name, params, body)` building a `within_hours` body | condition carries the params and a `BoolOp`/`And` body |
| params-only `Condition(name, params)` | body is `EmptyConditionBody` |
| evaluate a builder-produced `at_least` body | allow when the attribute satisfies the param |

**Done when:** build clean; both overloads coexist; the m0/02 builder test still passes.

---

### Task 6: Type-check condition bodies in `SchemaValidator`

- [ ] **Files:** create `src/Custodex.Core/Validation/ConditionBodyChecker.cs`; modify `src/Custodex.Core/Validation/SchemaValidator.cs`. Test: `tests/Custodex.Core.Tests/Validation/ConditionBodyCheckTests.cs`.

**Produces:** `static ConditionBodyChecker.Check(ConditionDef) → IReadOnlyList<string>`, invoked from `Validate` over every `ConditionDef` whose body is not `EmptyConditionBody`.
**Consumes (see README):** the body nodes (Task 1), `ConditionDef`, `ConditionParam`, `ConditionType`.

**Behavior:** statically infers each node's kind (bool/number/string/timestamp/unknown) and reports a `ParamRef` to an undeclared parameter, a `Compare`/`Arithmetic`/`BoolOp`/`HourOf`/`Not` over incompatible kinds, and a top-level body whose kind is not bool. An `AttributeRef` is kind-unknown (its type is only known at request time), so comparisons involving an attribute are not statically rejected — the runtime evaluator owns that. `EmptyConditionBody` is skipped.

**Cases to pin:**

| Setup | Expect |
|---|---|
| well-typed `within_hours` body | valid |
| body references an undeclared parameter | invalid; error names it |
| body compares a string param to a number | invalid |
| top-level body is non-boolean | invalid |
| params-only (`EmptyConditionBody`) condition | valid (skipped) |

**Done when:** build clean; body type errors caught; m0/03 and m0/02 tests still green.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] The body AST is CEL-shaped and derives from `Custodex.Abstractions.ConditionExpr`.
- [ ] `ConditionEvaluator` evaluates `within_hours`, `at_least`, and `is_creator`; a missing attribute or type mismatch is a deny with a diagnostic, not an exception.
- [ ] `CelConditionEvaluator` adapts the static evaluator to the `IConditionEvaluator` seam (allow→true, deny/error→false).
- [ ] Ambient time enters only through `RequestContext.Now`.
- [ ] `SchemaBuilder.Condition(name, params, body)` attaches a real body; the params-only overload still produces `EmptyConditionBody`.
- [ ] `SchemaValidator` type-checks non-empty condition bodies and skips `EmptyConditionBody`.
