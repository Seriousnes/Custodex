# M1/05 — CTE Check Path

**Goal:** Implement `NpgsqlCteAuthorizer.CheckAsync` and `BatchCheckAsync` — the Postgres recursive-CTE primary path for point Check (spec §7.1) — with the **same `IAuthorizer` Check semantics as the M0/05 `EngineDrivenAuthorizer` oracle**. The CTE computes reachability; the boolean algebra (union / intersection / exclusion / conditioned / arrow-into-sub-permission) and condition evaluation are composed in C#, per the seam decided by the M1/02 spike.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach (the seam decided in M1/02 — see that plan's boxed paragraph):** the recursive CTE expands reachability only (nested subject-set membership, arrow edges). `NpgsqlCteAuthorizer` is a near-line-for-line port of `EngineDrivenAuthorizer` with two substitutions: subject-set expansion runs in SQL via `CteReachability` instead of a C# loop over fetched tuples; and it opens its own `NpgsqlConnection` per Check, reused across the whole recursive walk. Everything else — the permission walk, cycle/depth guards via the reused `EvalContext`, condition evaluation through `IConditionEvaluator` — is identical, so the answers match. `Arrow(rel, perm)` follows the `rel` edges and recurses into each related object's full `perm` expression, so an inner exclusion under an arrow is always honoured.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Reuses `Custodex.Core` (`SchemaIndex`, `EvaluationOptions`, `EvalContext`/`EvalFrame`, `IConditionEvaluator`).

**Global Constraints:** see `../README.md` → Global Constraints. Cycle vs depth match the oracle: a re-entered frame on the current DFS path is pruned to `false` (never an exception); the depth bound throws `EvaluationLimitException`. Both come from the reused `EvalContext`.

**Dependencies:** builds on `m0/01` (Abstractions), `m0/05` (`SchemaIndex`, `EvalContext`, `EvaluationOptions`, `IConditionEvaluator`, the oracle's algebra it must match — see README), M1/01–M1/04 (schema, UoW, stores), and the M1/02 seam.

> **Calibration.** The recursive-CTE SQL is the hardest, least-certain code in the project. The **tests are the spec**: each pins behaviour the oracle already proves. The SQL and C# walk are the approach **validated by the M1/02 spike and the M1/08 differential harness**, not guaranteed-correct copy-paste. `NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer` is the claim; M1/08 is its proof. A divergence is a CTE bug — fix the path, the oracle is the spec.

---

### Task 1: Core project reference + the reachability store primitive

- [ ] **Files:** add the `Custodex.Core` project reference to `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj`; create `src/Custodex.Storage.Postgres/CteReachability.cs`; test `…Tests/Cte/CteReachabilityTests.cs`.

**Produces:** the `Custodex.Storage.Postgres → Custodex.Core` reference (decided in M1/02), and `CteReachability` with two static methods:
- `SubjectsThroughRelationAsync` — distinct **leaf** subjects (concrete + wildcard) reachable through `obj#relation`, expanding nested subject-sets in SQL; each returned `SubjectRef` carries identity only.
- `EdgesThroughRelationAsync` — the direct (non-expanded) tuples on `obj#relation`, carrying any condition; their subjects are the related objects for arrow edge-following.

**Consumes (see README):** the `relation_tuples` schema (M1/01); `RelationTuple`/`SubjectRef`/`ConditionRef`; `Json` (M1/04).

**Behavior** (spec §7.1): two methods because relation resolution needs **expanded leaves** (nested groups followed in SQL) while arrow edge-following needs **direct** structural tuples (their subjects are the objects to recurse into). The recursive CTE is the spike's proven reachability shape hardened for production: it carries each leaf's condition columns through and hard-filters `store + tenant`. Both accept an optional `NpgsqlTransaction` so a caller inside a write can read its own pending state.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `doc#viewer@group:staff#member`, staff→vets, vets→{userX,userY} | leaves `{userX, userY}`; no subject-set rows |
| `obj#rel@related:id` (structural edge) | edges return the related object unexpanded |

**Done when:** build clean; both cases pass (Postgres required); also asserted by the M1/08 harness.

---

### Task 2: `NpgsqlCteAuthorizer` skeleton + relation resolution + bare-RelationRef Check

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.cs` and `…/NpgsqlCteAuthorizer.Expr.cs`; test `…Tests/Cte/CteMembershipTests.cs`.

**Produces:** `NpgsqlCteAuthorizer : IAuthorizer` (`partial`, split by operation) with constructor `(connectionString, ISchemaStore, IAttributeStore, IConditionEvaluator, EvaluationOptions? = null)`. This task lands `CheckAsync`, `CheckPermissionAsync`, the relation-resolution primitive `ResolveRelationAsync`, a minimal `EvalExprAsync` handling `RelationRef`, and stubs for the other operations.
**Consumes (see README):** `CteReachability` (Task 1); `SchemaIndex`, `EvalContext`/`EvalFrame`, `EvaluationOptions`, `IConditionEvaluator` from Core; `ISchemaStore`/`IAttributeStore`.

**Behavior:** `CheckAsync` loads the active schema into a `SchemaIndex` (throwing `UnknownTypeException` when no active schema exists), opens one connection, and walks the permission under a fresh `EvalContext`, memoizing per `(object, permission, subject)` frame except when building an explain trace. `ResolveRelationAsync` answers "does subject `S` fill `obj#relation`?": it is **guarded against relation cycles on the current path** (`EvalContext.TryEnterRelation`, pruning to `false`), then — when no direct tuple on this relation carries a condition — takes the fast path (SQL expands nested subject-sets; match the leaf set, honouring wildcards by type); otherwise it walks the direct edges in C#, evaluating each tuple's condition and recursing into subject-sets, so conditions are applied exactly as the oracle does. A carried condition is evaluated via `IConditionEvaluator` against synced attributes + request context and latches `EvalContext.ConditionTouched`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| direct user grant | granted user allowed; other denied |
| wildcard `viewer@user:*` | any user allowed |
| nested group `viewer@group#member`, transitively to a user | that user allowed; outsider denied |
| membership cycle `a→b→a` | denies the probe subject without throwing |

**Done when:** build clean; all four cases pass (Postgres required); each point decision maps to a `ConformanceCase` row.

---

### Task 3: Full algebra — Union, Intersect, Exclude, Arrow, Conditioned

- [ ] **Files:** modify `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Expr.cs`; test `…Tests/Cte/CteAlgebraTests.cs`.

**Produces:** the complete `EvalExprAsync` over all six node kinds, plus `EvalArrowAsync` and a branch-condition gate, with the oracle's operator semantics.
**Consumes (see README):** `ResolveRelationAsync`, `CheckPermissionAsync` (Task 2); `CteReachability.EdgesThroughRelationAsync`; the `PermExpr` operators.

**Behavior** (spec §7.1; byte-for-byte the M0/05 semantics): Union OR-short-circuits, Intersect/Exclude AND-short-circuit, `Conditioned` gates an inner result on a branch-level condition (empty params, evaluated against attrs + context), and `Arrow(rel, perm)` follows the `rel` edges (evaluating each edge's condition) and recurses into each related object's **full** `perm` expression when `perm` is a permission, else a direct relation resolve when it names a relation. The discriminator (`Arrow_sees_inner_exclusion_on_the_related_object`) is the case the M1/02 spike proved naive SQL gets wrong.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `access = viewer + editor`, subject is editor | allowed; non-member denied |
| `access = vet & trained`, subject in both vs one | both → allow; one → deny |
| `access = viewer - blocked`, viewer-and-blocked subject | denied; viewer-only allowed |
| `edit = enclosure->edit`, related `edit = editor - blocked`, editor-and-blocked subject | **denied** (inner exclusion seen through the arrow); editor-only allowed |
| arrow target names a relation, gated by `is_quarantine@user:*` | any user allowed (arrow falls back to a relation resolve) |

**Done when:** build clean; all five cases pass (Postgres required); the discriminator is also covered by the M1/08 harness.

---

### Task 4: `BatchCheckAsync` and the Explain trace

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Batch.cs`; wire explain into `CheckAsync`; test `…Tests/Cte/CteBatchAndExplainTests.cs`.

**Produces:** `BatchCheckAsync` (one shared `EvalContext` memo and one connection across all items, results in request order) replacing the stub; and the explain path — `CheckAsync` with `Explain=true` returns a populated `ExplainNode` tree, built by passing a non-null explain sink through `CheckPermissionAsync` (already wired in Tasks 2–3).
**Consumes (see README):** `CheckPermissionAsync`, `EvalContext`, `SchemaIndex`, `BatchCheckRequest`/`CheckItem`, `ExplainNode`.

**Behavior:** the batch memo is per-batch, not shared across separate `CheckAsync` calls, matching the oracle.

**Cases to pin:**

| Setup | Expect |
|---|---|
| batch of three mixed items (via-group true, false, direct true) | one result per item, in order, with the expected allow/deny |
| Check with `Explain=true` on a granted permission | populated trace whose root description names `object#permission` |

**Done when:** build clean; both cases pass (Postgres required).

---

### Task 5: Worked-example parity over Postgres

- [ ] **Files:** test `…Tests/Cte/CteWorkedExampleParityTests.cs` (no production code).

**Consumes (see README):** `NpgsqlCteAuthorizer`, the stores, `SchemaBuilder`.

**Behavior:** encodes the spec §12.1 role-grant-over-category and §12.5 quarantine-gate worked examples (the shapes M0/09 proves against the oracle) directly over Postgres, asserting the CTE path returns the spec's truth. A guard that the CTE path agrees with the named acceptance cases before the M1/08 harness generalizes to random schemas.

**Cases to pin:**

| Setup | Expect |
|---|---|
| §12.1 role grant resolved through the arrow | group member allowed; outsider denied |
| §12.5 gate, trained member vs untrained | trained member allowed; untrained denied |

**Done when:** build clean; both cases pass (Postgres required).

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors; `Custodex.Storage.Postgres` references `Custodex.Core`.
- [ ] Reachability expands nested subject-sets in SQL and surfaces wildcard leaves; arrow edges return unexpanded (Task 1).
- [ ] Check matches the oracle on direct/wildcard/nested/cycle membership (Task 2) and the full algebra (Task 3).
- [ ] The arrow-inner-exclusion discriminator denies the blocked subject (the case naive SQL gets wrong).
- [ ] Conditioned tuples (direct and nested) are evaluated in C# via `IConditionEvaluator`, latching `ConditionTouched`.
- [ ] Relation cycles prune to deny; the depth bound throws `EvaluationLimitException` — both via the reused `EvalContext`.
- [ ] `BatchCheckAsync` shares one memo/connection in order; `Explain=true` returns a populated trace (Task 4).
- [ ] §12.1 and §12.5 worked examples pass over Postgres (Task 5).
