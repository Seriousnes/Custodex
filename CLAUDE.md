# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Custodex is a runtime-configurable **ReBAC (relationship-based) + ABAC (condition-based) authorization engine**, modelled on Google Zanzibar (the lineage behind SpiceDB / OpenFGA / Permify). It ships as a reusable .NET library and, later, a standalone gRPC/REST service. It contains **zero domain concepts** — a consuming app supplies its permission model as a *schema* (entity types, relations, permissions, conditions) and its authorization data as *tuples* + *attributes*. The first consumer is a multi-tenant zoo-management SaaS, but the engine knows nothing about zoos.

The engine answers four questions: **Check** (point decision), **ListObjects** (which objects a subject may act on), **ListSubjects** (who may act on an object), and **BatchCheck**.

## The design docs are the source of truth

This is a plan-driven build. Before non-trivial work, read:

- `docs/superpowers/specs/2026-06-23-rebac-engine-design.md` — the full design (architecture, evaluation algebra, storage model, the six **worked examples** in §12 that double as acceptance cases).
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

### Conformance suite (`tests/Custodex.Conformance`)

Declarative `ConformanceCase` records (`schema + tuples + attributes → expected Check result`) run through `ConformanceRunner` against the in-memory engine. The spec's six worked examples (§12.1–12.6) are encoded as named cases (`WorkedExamples*.cs`). This is the portable acceptance bar any future storage provider must pass. `Properties/` holds the CsCheck algebra-invariant property tests.

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

## Current state

Branch `feat/m0-engine-core`. M0 (engine core: abstractions, schema model + builder + validation, in-memory providers, engine-driven traversal for all four ops, conditions, caching, conformance + property harness) is largely in place. `Storage.Postgres`, `Service`, and `Client` are scaffolded shells awaiting M1/M3.
