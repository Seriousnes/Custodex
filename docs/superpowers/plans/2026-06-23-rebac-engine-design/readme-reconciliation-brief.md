# Brief: Reconcile the foundation README with shipped M0/M1 code

> A self-contained kickoff for a fresh session. It assumes no prior conversation. Read it top to bottom, confirm each fact against source yourself, then make the edits and verify.

## Why this exists

Custodex is a runtime-configurable ReBAC + ABAC authorization engine, shipped as a reusable .NET library (`net10.0`, C# 14). The build is plan-driven: every milestone plan under `docs/superpowers/plans/2026-06-23-rebac-engine-design/` references the **foundation README** in that same folder — its canonical type names, signatures, and its "Post-dispatch contract reconciliations" — *by name*, instead of restating them. The README is therefore the single source of truth the plans defer to.

M0 (engine core) and M1 (Postgres provider) are implemented and committed. Two statements in the README no longer match the code that shipped. Because the plans defer to the README, each drift silently misleads any plan pointing at it. This brief is the complete set of changes to bring the README back into agreement with the code; both are README-only edits — the code is the source of truth, so the docs move.

## Orient yourself first

- Read `README.md` in this folder. You will edit two places: the **"Canonical public contract" → Schema model (canonical AST)** block, and **"Post-dispatch contract reconciliations" item 1**.
- Build/verify with `dotnet build Custodex.slnx`.
- Planning docs (the README, the milestone plans, this brief) may freely cite spec section numbers and milestone tags. The project's "no comments / no doc references" rule applies **only to source files** — do not touch source comments as part of this work.
- Each drift below carries a command to confirm the shipped reality. Run it and read the cited files before editing — do not trust this brief blind.

## Drift 1 — AST JSON polymorphism

- **README currently says:** in *Canonical public contract → Schema model (canonical AST)*, the comment above `public abstract record PermExpr;` states the AST carries `System.Text.Json` polymorphism via `[JsonPolymorphic]` / `[JsonDerivedType]` attributes, *"(Implemented in m0/01.)"*
- **Shipped reality:** no such attributes exist on `PermExpr` / `ConditionExpr` in `Custodex.Abstractions`. The AST is (de)serialized by a hand-written converter, `src/Custodex.Storage.Postgres/PermExprJsonConverter.cs`, which landed in M1.
- **Confirm:** `grep -rn "JsonPolymorphic\|JsonDerivedType" src/Custodex.Abstractions` (expect no matches), then read `src/Custodex.Storage.Postgres/PermExprJsonConverter.cs`.
- **Edit:** rewrite that README comment to describe converter-based serialization owned by the provider (name `PermExprJsonConverter`), and drop the "attributes / implemented in m0/01" claim.

## Drift 2 — `IConditionEvaluator` shape

- **README currently says:** *Post-dispatch contract reconciliations* item 1 specifies `ConditionResult Evaluate(ConditionDef def, tupleParams, resourceAttributes, context)` with `ConditionResult(bool Passed, string? Diagnostic)`.
- **Shipped reality:** the interface returns **`bool`**, not `ConditionResult`. The `ConditionResult` record (its field is **`Allowed`**, not `Passed`) belongs to the separate static `ConditionEvaluator`. A `CelConditionEvaluator` adapter bridges the static evaluator to the `IConditionEvaluator` seam the authorizers depend on.
- **Confirm:** read `src/Custodex.Core/Conditions/IConditionEvaluator.cs`, `src/Custodex.Core/Conditions/ConditionEvaluator.cs`, and the `CelConditionEvaluator` it adapts. Transcribe the exact signatures from these files.
- **Edit:** rewrite reconciliation item 1 to the shipped signatures (bool-returning interface; `ConditionResult.Allowed` on the static evaluator; the `CelConditionEvaluator` adapter as the registered `IConditionEvaluator`).

## Verify, then you are done

- Re-run the two confirmation commands; the README text now agrees with each.
- `dotnet build Custodex.slnx` is green (these are README-only edits; nothing in `src/` changes in this pass).
- A read-through of the README finds no remaining claim that contradicts M0/M1 source.

## Commit

One `docs:` commit when finished, e.g. `docs: reconcile README with shipped M0/M1 code`. Conventional Commits, with the trailer:

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

Note: `docs` is gitignored in this repo, but the README is already tracked (force-added historically). Stage the edit with `git add -u`.
