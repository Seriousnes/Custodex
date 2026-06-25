# M0/09 — Conformance & Property Harness

**Goal:** A declarative conformance case format (`schema + tuples + attributes + (subject, object, permission, context) → expected`) with a runner over the in-memory engine, and CsCheck property tests asserting algebra invariants against the engine-driven authorizer — the portable acceptance bar any future storage provider must pass. The supporting domain-neutral test infrastructure (`TestWorld`) and the vocabulary guard that holds the line are documented here.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** A `ConformanceCase` record carries everything to run one decision deterministically; `ConformanceRunner` materializes it against `Custodex.Storage.InMemory` + `EngineDrivenAuthorizer`, routes the schema through `SchemaValidator` (so a malformed case fails loudly), and runs `CheckAsync`. CsCheck generators build random tuple sets over small fixed schemas and assert algebra laws (`a − a = deny`, wildcard grants everyone, nesting reachability, union monotonicity over exclusion-free permissions) using the engine as the executable specification. All test identifiers are domain-neutral, vended by `Custodex.TestKit.TestWorld`; a guard test scans the whole `tests/` tree and fails if industry vocabulary reappears.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly, CsCheck (MIT). Backed by `Custodex.Storage.InMemory` and the `EngineDrivenAuthorizer` + `NullConditionEvaluator`.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/02–m0/07 (`SchemaBuilder`, `SchemaValidator`, the in-memory stores, `EngineDrivenAuthorizer`, `NullConditionEvaluator`, condition evaluator). The conformance suite and property tests live **inside `tests/Custodex.Core.Tests`** under `Conformance/` and `Properties/`; the domain-neutral identifier factory lives in the separate `tests/Custodex.TestKit` support library (not a test project) referenced by the test projects. There is no standalone conformance project.

> **Calibration — the rigor lives here.** The engine in m0/05–m0/07 is a candidate proven by these cases and properties. They are the durable specification: if the engine disagrees with a case, the engine is wrong unless the case is shown to misread the spec.

---

### Task 1: The declarative case format

- [ ] **Files:** create `tests/Custodex.Core.Tests/Conformance/ConformanceCase.cs`; test `…/Conformance/CaseFormatTests.cs`.

**Produces:** the `ConformanceCase` and `AttributeSeed` records (namespace `Custodex.Core.Tests.Conformance`).
**Consumes (see README):** `Schema`, `RelationTuple`, `EntityRef`, `SubjectRef`, `RequestContext`.

**Behavior:**
- `ConformanceCase` carries, in order: `Name`, `Schema`, `Tuples`, `Attributes`, `Object`, `Permission`, `Subject`, `Now`, `Context`, `Expected` (a `bool`). This is the per-decision unit the rest of M0+ and any future provider runs against.
- `AttributeSeed(EntityRef Object, IReadOnlyDictionary<string, object?> Attributes)` seeds one object's synced resource attributes before the query.

**Cases to pin:**

| Setup | Expect |
|---|---|
| construct a case with one tuple and `Expected: true` | `Name`/`Expected`/`Tuples.Count` round-trip; identifiers are `TestWorld`-vended, never literals |

**Done when:** build clean under TreatWarningsAsErrors; case round-trips.

---

### Task 2: The conformance runner

- [ ] **Files:** create `tests/Custodex.Core.Tests/Conformance/ConformanceRunner.cs`; test `…/Conformance/RunnerTests.cs`.

**Produces:** `ConformanceRunner` (static) with `RunAsync(ConformanceCase, CancellationToken)` and `AssertAsync(ConformanceCase)`, plus condition-injecting overloads `RunAsync(ConformanceCase, IConditionEvaluator, CancellationToken)` / `AssertAsync(ConformanceCase, IConditionEvaluator)`.
**Consumes (see README):** `EngineDrivenAuthorizer`, `NullConditionEvaluator` and the real condition evaluator (m0/06), the in-memory stores, `SchemaValidator` (m0/03).

**Behavior:**
- Validate the case schema through `SchemaValidator.Validate`; throw `SchemaValidationException` on an invalid schema, so a malformed case fails loudly rather than producing a silent wrong decision — the suite doubles as a validation acceptance test.
- Materialize the case: set the active schema, write the tuples, seed each `AttributeSeed`, commit, build the authorizer, run `CheckAsync`. The plain overload uses `NullConditionEvaluator`; the overload lets a case inject the real `CelConditionEvaluator` (m0/06) to exercise conditions.
- The tenant is the per-test `TestWorld.Tenant`, not a hardcoded value, so parallel cases never collide.
- `AssertAsync` asserts `Allowed == Expected` with the case name in the failure message.

**Cases to pin:**

| Setup | Expect |
|---|---|
| direct grant, subject matches | `Allowed == true`; `AssertAsync` does not throw |
| empty tuples, subject absent | `Allowed == false` |

**Done when:** build clean; both runner paths assert correctly; invalid-schema case throws.

---

### Task 3: CsCheck algebra-invariant properties

- [ ] **Files:** create `tests/Custodex.Core.Tests/Properties/AlgebraGenerators.cs`, `…/Properties/AlgebraInvariantTests.cs`.

**Produces:** CsCheck generators over small fixed schemas (an exclusion/condition-free `Monotone` schema, a self-exclusion `a − a` schema, and a nested-group-chain tuple builder) plus the four algebra-invariant property tests.
**Consumes (see README):** `EngineDrivenAuthorizer`, `NullConditionEvaluator`, the in-memory stores; CsCheck `Gen`/`SampleAsync`; `TestWorld` for neutral identifiers (wrapped in an `AlgebraWorld` helper).

**Behavior** (spec §7, algebra semantics):
- The generators draw users from a small pool and build random tuple sets; the engine is the executable spec the laws are checked against.
- **Property scoping:** `Exclude` is non-monotone, so monotonicity is asserted only over an exclusion- and condition-free permission (`view = viewer`). The other laws hold generally.

**Cases to pin** (each a CsCheck property over random inputs):

| Property | Holds for |
|---|---|
| `a − a = deny` | any subject, even one that holds `viewer` |
| wildcard grants everyone | any random user under a `user:*` grant |
| nesting reachability | a user at the bottom of a random-length nested `group#member` chain reaches a top-group grant |
| union monotone (exclusion/condition-free) | granting `viewer` only flips deny→allow, never the reverse |

**Done when:** build clean; all four properties pass — a CsCheck counterexample is a genuine engine bug to minimize and fix in m0/05.

---

### Task 4: Domain-neutral test infrastructure and the vocabulary guard

- [ ] **Files:** the `Custodex.TestKit` support library (`tests/Custodex.TestKit/TestWorld.cs`, `…/NeutralIdentifiers.cs`); tests `tests/Custodex.Core.Tests/TestKit/TestWorldTests.cs` and `…/Guards/DomainVocabularyGuardTests.cs`.

**Produces:** `TestWorld` — a seeded Bogus façade vending neutral, deterministic identifiers and absorbing store-wiring; `NeutralIdentifiers` (the Bogus dataset behind it); the `TestWorld` golden-value pin; and the vocabulary guard.
**Consumes (see README):** `Schema`/`SchemaBuilder`, the in-memory stores, `EngineDrivenAuthorizer`, `TenantContext` and the reference types — wrapped behind `TestWorld`'s `BuildAsync`/`Tuple`/`Check` helpers.

**Behavior:**
- `TestWorld.New([CallerMemberName])` seeds deterministically from the test method name (stable across runs, distinct per test, parallel-safe via a per-world `Randomizer`). It vends unique entity-types, relations, permissions, condition/param names, subject/object ids, a `Version`, and a `Tenant`, plus tuple/check/build helpers. The only literal that stays anywhere is the reserved wildcard id `"*"`.
- `TestWorldTests` pins `TestWorld`'s output against golden values under a fixed seed, so a Bogus upgrade or a seeding change fails loudly.
- `DomainVocabularyGuardTests` scans every `.cs` under `tests/` (excluding build output and stale `(2)` copy artifacts) and fails if banned industry terms reappear (animal/enclosure/vet/quarantine, species names, dispense/medication/drug, named people/places, literal ids).

**Cases to pin:**

| Setup | Expect |
|---|---|
| `TestWorld.New()` under the fixed golden seed | each vended identifier equals its pinned golden value |
| many `TestWorld` draws in one world | identifiers are unique within each category |
| scan the `tests/` tree | no banned domain term appears in any source file |

**Done when:** build clean; golden values match; uniqueness holds; the guard scans the tree green.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors; `dotnet test tests/Custodex.Core.Tests` green.
- [ ] The case format carries schema + tuples + attributes + (subject, object, permission, context) → expected.
- [ ] The runner routes through `SchemaValidator` (m0/03) so a malformed case fails loudly; a condition-injecting overload exists.
- [ ] Property scoping is correct: monotonicity restricted to exclusion/condition-free permissions; `a − a = deny`, wildcard, and nesting hold generally.
- [ ] All test identifiers are `TestWorld`-vended; the vocabulary guard scans the `tests/` tree green; the `TestWorld` golden pin holds.
