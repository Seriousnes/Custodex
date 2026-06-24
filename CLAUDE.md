# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Custodex is a runtime-configurable **ReBAC (relationship-based) + ABAC (condition-based) authorization engine**, modelled on Google Zanzibar (the lineage behind SpiceDB / OpenFGA / Permify). It ships as a reusable .NET library and, later, a standalone gRPC/REST service. It contains **zero domain concepts** — a consuming app supplies its permission model as a *schema* (entity types, relations, permissions, conditions) and its authorization data as *tuples* + *attributes*.

The engine answers four questions: **Check** (point decision), **ListObjects** (which objects a subject may act on), **ListSubjects** (who may act on an object), and **BatchCheck**.

## The design docs are the source of truth

This is a plan-driven build. Before non-trivial work, read:

- `docs/superpowers/specs/2026-06-23-rebac-engine-design.md` — the full design (architecture, evaluation algebra, storage model, and the six **worked examples** in §12).
- `docs/superpowers/plans/2026-06-23-rebac-engine-design/README.md` — the **canonical public contract**: every normative type name and signature (`IAuthorizer`, `Schema` AST, storage interfaces, etc.), global constraints, and project layout. Plans reference types by their exact names here. **If a change needs a new shared type, add it to this README first, then propagate.**
- `docs/.../m0/`–`m3/` — milestone-by-milestone implementation plans (`mN/0X-*.md`).

The README's "Post-dispatch contract reconciliations" section overrides any conflicting plan text — read it when a plan and the contract disagree.

## Build / test / run

Always pass the solution file explicitly — `Custodex.slnx` (the new XML `.slnx` format).

```bash
dotnet build Custodex.slnx
dotnet test  Custodex.slnx                              # whole suite
dotnet test  tests/Custodex.Core.Tests                 # one project
dotnet test  tests/Custodex.Core.Tests --filter "FullyQualifiedName~ListObjects"   # one class/test
dotnet run --project src/Custodex.AppHost              # local Aspire stack (Postgres + Service), M1+
```

- **Target:** `net10.0`, C# 14. `Nullable`, `ImplicitUsings`, and **`TreatWarningsAsErrors=true`** are all on (in `Directory.Build.props`) — a warning fails the build.
- **Test stack:** xUnit + **Shouldly** assertions (not FluentAssertions — it's banned by license). Property tests use **CsCheck**. Postgres integration tests (M1+) use **Testcontainers** and need Docker running.
- Python is not available on this machine; for throwaway scripts use PowerShell or `dotnet run script.cs`.

**Untracked cruft:** directories named `... (2)` under `src/` and `tests/` are stale copy artifacts, not tracked by git and not in `Custodex.slnx`. Ignore them; they are not part of the build.

## Architecture

Three conceptual layers (spec §3): the **engine** (generic, this repo), the **application schema** (a versioned developer artifact), and **tenant data** (tuples/attributes, runtime-configurable with no engineers in the loop).

### Project dependency direction

```
Custodex.Abstractions   ← contracts ONLY (interfaces + records, no logic). The dependency for consumers and 3rd-party providers.
Custodex.Core           ← the engine. Depends only on Abstractions. Zero domain concepts, zero DB code.
Custodex.Storage.InMemory  ← in-memory providers for fast unit tests + the correctness oracle's backing store.
Custodex.Storage.Postgres  ← (M1) Dapper/Npgsql provider. May reference Core (for SchemaIndex etc.); Core never references it.
Custodex.Service / .Client ← (M3) gRPC/REST host + client, both over the SAME Core.
Custodex.AppHost / .ServiceDefaults ← .NET Aspire orchestration + shared OTel/health defaults.
tests/Custodex.TestKit ← test-only support lib (not a test project): a Bogus-backed TestWorld vending neutral, deterministic identifiers. Referenced by every *.Tests project.
```

`Storage.Postgres` is allowed to reference `Core`; the rule the spec enforces is **no database code inside Core**, not "nothing may reference Core."

### Two evaluation paths, one set of semantics

This is the central architectural idea. The algebra (union `+`, intersection `&`, exclusion `-`, arrow/traversal `rel->perm`, group nesting, conditions) has two execution paths that **must produce identical results**:

1. **Engine-driven traversal** (`Custodex.Core`, `EngineDrivenAuthorizer`) — a C# walk over the permission expression issuing batched indexed lookups through `IRelationStore`. This is the portable path, the in-memory test path, and the **correctness oracle**.
2. **Postgres recursive CTEs** (`Custodex.Storage.Postgres`, M1) — the primary production path.

A **differential property-based harness** (M1/08, M2/06) generates random schemas + tuples and asserts `CTE ≡ engine-driven oracle ≡ reverse index`. The fast paths are trusted only when the oracle agrees. When implementing an algorithm-heavy path, the **tests are the durable spec**; the implementation is a candidate proven by the harness.

### Engine internals (Custodex.Core)

- `EngineDrivenAuthorizer` is a `partial` class split by operation: `.cs` (Check), `.Expr.cs`, `.Batch.cs`, `.ListObjects.cs`, `.ListSubjects.cs`, `.Reverse.cs`. The Check walk is in the base file — start there.
- `SchemaIndex` wraps a `Schema` AST in lookup dictionaries (`Type`/`Permission`/`Relation`/`Condition`), throwing the typed `Unknown*Exception`s. Built per request from the active schema.
- `EvalContext` carries per-request state: memoization, cycle guards, depth bound, and the `ConditionTouched` latch.
- **Conditions** (`Conditions/`) are evaluated through the `IConditionEvaluator` seam: `NullConditionEvaluator` (always pass), `ConditionEvaluator`/`CelConditionEvaluator` (the real CEL-shaped predicate evaluator). A failed/missing-attribute condition is **default-deny with a diagnostic, never an exception**.
- **Caching** (`Caching/`): `CachingAuthorizer` decorates an `ICacheableAuthorizer` (the internal `CheckInternalAsync` seam returning `(Allowed, ConditionTouched)`). Only **unconditioned** results are cached; invalidation is a coarse per-`(store, tenant)` epoch.
- **Schema authoring**: fluent `SchemaBuilder` (`.Type().Relation().Permission().Condition().Build()`) produces the canonical `Schema` AST. The AST (`PermExpr`/`ConditionExpr`) uses `System.Text.Json` polymorphism so it serializes to jsonb and to the DSL (M3).

### Domain-neutral tests and the conformance harness

Tests carry **no industry vocabulary**: every schema/tuple/query identifier is generated by `Custodex.TestKit.TestWorld` (a seeded Bogus façade), never hardcoded. `TestWorld.New()` seeds deterministically from the test method name (stable across runs, distinct per test, parallel-safe via per-world `Randomizer`) and vends unique entity-types/relations/permissions/ids plus `BuildAsync`/`Tuple`/`Check` helpers that absorb the store-wiring boilerplate. The only literal that stays is the reserved wildcard id `"*"`. Two guards hold the line: `Guards/DomainVocabularyGuardTests` scans the whole `tests/` tree and fails if banned domain terms reappear, and `TestKit/TestWorldTests` pins `TestWorld`'s output with golden values so a Bogus upgrade or seeding change fails loudly.

Declarative `ConformanceCase` records (`schema + tuples + attributes → expected Check result`) run through `ConformanceRunner` against the in-memory engine — the portable acceptance bar any future storage provider must pass. The harness and the CsCheck algebra-invariant property tests live in `tests/Custodex.Core.Tests` under `Conformance/` and `Properties/`.

## Conventions that bite if missed

- **Determinism in evaluation:** no `DateTime.Now` / `Guid.NewGuid()` inside the evaluation path. Ambient time enters only via `RequestContext.Now`.
- **Identifiers** (`store`, `tenant`, `type`, `id`, `relation`, `permission`, condition names) are non-empty `string`s compared **ordinal**. The id `"*"` is the reserved **wildcard** (`type:*` = every instance of that type in the tenant).
- **Tenant isolation** is a hard invariant: every storage operation filters on `(store_id, tenant_id)` — non-optional.
- **Async:** all I/O methods are `async` and take a trailing `CancellationToken ct = default`.
- **Deny vs error:** allow/deny is always a `CheckResult` return value. Unknown type/relation/permission, invalid schema, and tripped depth/cycle guards are typed **exceptions**.
- `store`/`tenant` are **not** fields on `RelationTuple` — they're carried by the operation via `TenantContext`, so tuple values are reusable across stores in tests.
- **Commits:** Conventional Commits (`feat:`/`test:`/`refactor:`/`chore:`), one green commit per TDD step. Co-author trailer required:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- **Licensing:** Apache-2.0; no dependency under a non-permissive or paid license (explicitly **not** FluentAssertions v8+). No EF Core in any shipped package.

## Coding Style and Conventions

### Comment Style
- In C# code, comments are gated by visibility: a declaration may carry a comment **only if it is visible to another assembly** — `public` types (classes, records, structs, enums, interfaces, delegates) and the `public`/`protected` members of a publicly-visible type. Everything not visible outside its assembly — `internal`, `private`, `private protected`, file-local types, local functions, lambda bodies, and any inline comment inside a method body — gets **no comment of any kind**: no `///`, no `//`, no `/* */`. Such code must carry its meaning through naming and structure.
- On that public surface the only permitted form is the XML documentation comment (`///`); `//` line comments and `/* */` block comments are never used anywhere. This ships as a NuGet library, so the public API surface *is* the product: every `public` type and interface — and its `public`/`protected` members — must carry one, above all in `Custodex.Abstractions` (the consumer-facing contract), because consumers read them as IntelliSense and on the package docs. Document the contract and the *why*, not the obvious.
- No source file — code, comment, SQL, or migration — may refer to a design decision, spec, plan, milestone, or any other document, nor to where, how, or why something was decided or documented (no spec section numbers, milestone tags like `M2`, "locked in", "contract gap", "see the design doc", etc.).

### Public API & extensibility
- This is a library others consume, extend, and test against — don't make it a closed box without good reason. Anything a consumer might implement, substitute, or mock must be exposed as an **interface**, and consumer-facing code depends on that interface, not the concrete type. Every seam already follows this: `IAuthorizer`, the storage providers (`IRelationStore` / `IAttributeStore` / `ISchemaStore` / `ICacheStore` / `IChangeLogStore`), `IConditionEvaluator`, and the `I*Manager` management seams.
- `sealed` stays the default for concrete implementations and for the closed data/AST records (`Schema`, and the `PermExpr` / `ConditionExpr` node hierarchies). Sealing these does **not** close the box: extension flows through the interfaces above, and the AST is a deliberately closed set the engine switches over exhaustively — a third-party `PermExpr` node would break that. Reserve open inheritance (`abstract` / `virtual`, unsealed) for hierarchies genuinely meant to be subclassed.

### Coding Style
- Use the latest language features, such as collection initialization (e.g. `arr = []`) where possible. Don't use `Array.Empty<T>`, `new List<T>()`, etc when the target type is known and could be initialized by `[]`.
- Use modern pattern matching
  - `if (obj is not null)` instead of `if (obj != null)`
  - `if (obj is { val: > 0 })` instead of `if (obj?.val > 0)`
  - `if (obj is T objT)` instead of `if (obj != null && obj is T) { var objT = (T)obj; ... }`
