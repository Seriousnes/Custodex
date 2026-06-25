# M0/02 — Schema Model & Fluent Builder

**Goal:** Stand up `Custodex.Core` and a fluent `SchemaBuilder` that produces the `Schema` AST defined in `Custodex.Abstractions`.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** the AST records live in `Custodex.Abstractions` (m0/01); the builder is construction logic in `Custodex.Core`. Permission terms chain by **union** by default; `Exclude`/`Intersect`/`Conditioned` wrap the accumulated expression. The builders are small, composable, and exist only to produce the canonical AST — they hold no evaluation logic.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/01 — the `Schema` AST records, `ConditionExpr` marker, and `Custodex.Abstractions` (see README).

---

### Task 1: Create `Custodex.Core` and reference Abstractions

- [ ] **Files:** wire `src/Custodex.Core` → `src/Custodex.Abstractions` reference, and `tests/Custodex.Core.Tests` → `Custodex.Core` + Shouldly; remove template leftovers. Test: `tests/Custodex.Core.Tests/CoreWiringTests.cs`.

**Produces:** the `Custodex.Core` assembly referencing `Custodex.Abstractions`.
**Consumes (see README):** the project shells already exist in `Custodex.slnx`.

**Behavior:** the engine project depends only on Abstractions (zero domain concepts, zero DB code). A placeholder type proves the reference resolves.

**Cases to pin:**

| Setup | Expect |
|---|---|
| reference `Custodex.Core` from the test project | the assembly is loadable and named `Custodex.Core` |

**Done when:** build clean under TreatWarningsAsErrors; wiring test passes.

---

### Task 2: Permission-expression builder

- [ ] **Files:** create `src/Custodex.Core/PermExprBuilder.cs`. Test: `tests/Custodex.Core.Tests/PermExprBuilderTests.cs`.

**Produces:** `PermExprBuilder` with `Relation(string)`, `Arrow(string, string)`, `Union(Action<PermExprBuilder>)`, `Intersect(Action<PermExprBuilder>)`, `Exclude(Action<PermExprBuilder>)`, `Conditioned(string)`, and `PermExpr Build()`.
**Consumes (see README):** `PermExpr`, `RelationRef`, `Union`, `Intersect`, `Exclude`, `Arrow`, `Conditioned`.

**Behavior:**
- Default chaining is **union**: each `Relation`/`Arrow` term unions onto the accumulated expression. `Intersect`/`Exclude` wrap the accumulated expression on the left with the sub-built expression on the right; `Conditioned(name)` wraps the accumulated expression. Sub-builders (`Union`/`Intersect`/`Exclude` taking an `Action`) build a nested expression. `Build()` on an empty builder throws `InvalidOperationException` (a permission must have at least one term).

**Cases to pin:**

| Setup | Expect |
|---|---|
| `.Relation(a).Arrow(rel, perm).Exclude(x => x.Relation(b))` | `Exclude(Union(RelationRef a, Arrow rel→perm), RelationRef b)` |
| `.Relation(a).Intersect(x => x.Relation(b)).Conditioned(c)` | `Conditioned(Intersect(RelationRef a, RelationRef b), c)` |
| `Build()` with no terms | throws `InvalidOperationException` |

**Done when:** build clean; the wrapping order holds.

---

### Task 3: Subject-filler and condition-param builders

- [ ] **Files:** create `src/Custodex.Core/SubjectFillerBuilder.cs`, `src/Custodex.Core/ConditionParamBuilder.cs`. Test: `tests/Custodex.Core.Tests/FillerAndParamBuilderTests.cs`.

**Produces:** `SubjectFillerBuilder` with `User()`, `Type(string)`, `SubjectSet(string type, string relation)`, `Wildcard(string type)`, `Build()`; `ConditionParamBuilder` with `Bool/Int/Long/Double/String/Timestamp(string name)` and `Build()`.
**Consumes (see README):** `SubjectTypeRef`, `ConditionParam`, `ConditionType`.

**Behavior:**
- `User()` is sugar for `Type("user")`. `SubjectSet(type, relation)` records a subject-set filler (`Relation` set); `Wildcard(type)` records a wildcard filler (`Wildcard = true`). The param builder maps each typed method to the matching `ConditionType`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `.User().SubjectSet(g, m).Wildcard(u)` | fillers contain a plain `user`, a `g#m` subject-set, and a `u` wildcard |
| `.Int(start).Int(end)` | two `Int` `ConditionParam`s in order |

**Done when:** build clean; fillers/params collected correctly.

---

### Task 4: Type and schema builders

- [ ] **Files:** create `src/Custodex.Core/EntityTypeBuilder.cs`; modify `src/Custodex.Core/SchemaBuilder.cs`. Test: `tests/Custodex.Core.Tests/SchemaBuilderTests.cs`.

**Produces:** `EntityTypeBuilder` with `Relation(string, Action<SubjectFillerBuilder>)` and `Permission(string, Action<PermExprBuilder>)`; `SchemaBuilder(string version)` with `Type(string, Action<EntityTypeBuilder>)`, a params-only `Condition(string, Action<ConditionParamBuilder>)`, and `Schema Build()`. Also the `EmptyConditionBody` placeholder record (`: ConditionExpr`) used by the params-only condition overload.
**Consumes (see README):** all builders above; `Schema`, `EntityTypeDef`, `RelationDef`, `PermissionDef`, `ConditionDef`, `ConditionExpr`.

**Behavior:**
- The builder produces the canonical `Schema` AST exactly as the contract's fluent example shows. The params-only `Condition` overload attaches `EmptyConditionBody` (a condition declared with parameters but no body); m0/06 adds the body-carrying overload alongside it.

**Cases to pin:**

| Setup | Expect |
|---|---|
| build the contract's animal schema | version, relation names in order, the `edit` permission resolves to an `Exclude`, the condition has two params |

**Done when:** build clean; the contract's fluent example produces the expected AST.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] The contract's fluent example in `../README.md` compiles and produces the expected AST.
- [ ] Default chaining is union; `Exclude`/`Intersect`/`Conditioned` wrap the accumulated expression; empty `Build()` throws.
- [ ] The params-only `Condition` overload attaches `EmptyConditionBody`; m0/06 attaches real bodies via a `Condition(name, params, body)` overload.
