# M0/05 — Engine-Driven Check

**Goal:** Implement engine-driven `CheckAsync` in `Custodex.Core` (`EngineDrivenAuthorizer : IAuthorizer, ICacheableAuthorizer`) that walks the `PermExpr` of an object's permission as a **pointwise recursive membership test** for the query subject, with cycle/depth guards, per-request memoization, `Explain` trees, and `Custodex.Diagnostics` telemetry. This is the portable correctness oracle.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** Check is **not** set materialization. It asks "is *this* subject a member of the permission's resolved set?" and recurses: `RelationRef` matches a tuple's subject directly, by wildcard `type:*`, or by expanding a subject-set `group:G#rel` (recurse into `G`'s relation); `Union`/`Intersect`/`Exclude` short-circuit boolean composition over sub-results; `Arrow(rel, perm)` resolves related objects via `rel` and recurses into *their* full permission expression — so an inner `- blocked` is always seen, which is exactly why this path is the oracle. `EngineDrivenAuthorizer` is a `partial` class split by operation across six files (`.cs` holds Check + membership helpers; `.Expr.cs` the algebra; `.Batch.cs`, `.ListObjects.cs`, `.ListSubjects.cs`, `.Reverse.cs` are filled by m0/07). This plan implements Check only; the list/batch operations are stubbed via `NotImplementedException` until m0/07.

**Cycle vs depth — two distinct guards** (spec §10.3 vs the contract's `EvaluationLimitException`):
- A **cycle** (the same `(object, permission, subject)` frame re-entered on the current DFS path — e.g. a nested-group loop) is normal data: it is **pruned and contributes `false`**, never an exception. A membership question that depends on itself cannot add a new grant.
- A **depth bound** exceeded (configurable, default 64) throws `EvaluationLimitException` — the hard limit of §10.3 surfaced as the typed exception.

**The condition seam** is `Custodex.Core.Conditions.IConditionEvaluator` with a `bool`-returning `Evaluate(ConditionDef, ConditionRef, IReadOnlyDictionary<string, object?> resourceAttributes, RequestContext)`. The authorizer depends on this injectable seam so `NullConditionEvaluator` works in pure-ReBAC tests and the condition-touched latch is uniform. m0/06 ships the real evaluator as a static `ConditionEvaluator` returning `ConditionResult`, adapted to this seam by `CelConditionEvaluator` (mapping allow→true, deny/error→false). Until m0/06 lands, the authorizer is constructed with `NullConditionEvaluator`.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly. Tests use the `Custodex.Storage.InMemory` provider (m0/04).

**Global Constraints:** see `../README.md` → Global Constraints (no `DateTime.Now`/`Guid.NewGuid()` in evaluation; ambient time only via `RequestContext.Now`).

**Dependencies:** builds on m0/01 (Abstractions + diagnostics), m0/02 (schema builder), m0/03 (validation), m0/04 (in-memory stores) — see README.

---

### Task 1: Evaluation context — memo, visited set, depth budget

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EvalContext.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/EvalContextTests.cs`.

**Produces:** `EvalContext` carrying the per-request memo (`EvalFrame → bool`), separate current-path visited sets for permission and relation frames (cycle guards), a depth counter against a bound, and a latching `ConditionTouched` flag; the `EvalFrame(EntityRef Object, string Permission, SubjectRef Subject)` readonly struct as the memo/visited key; `EvaluationOptions(int MaxDepth = 64)`; a disposable `PathScope` returned by the enter methods.
**Consumes (see README):** `EntityRef`, `SubjectRef`, `EvaluationLimitException`.

**Behavior:**
- `TryGetMemo`/`SetMemo` cache completed sub-checks. `TryEnter` (permission frames) and `TryEnterRelation` (relation frames) add the frame to their respective on-path set, enforce the depth bound (throwing `EvaluationLimitException` when exceeded), and return a `PathScope` whose disposal leaves the frame. Re-entering a frame already on its path returns false (a cycle the caller treats as non-contributing). `MarkConditionTouched` latches `ConditionTouched`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| set then get a memo | hit returns the stored result |
| enter the same frame twice on the path | second enter false (cycle); enterable again after the scope disposes |
| exceed `MaxDepth` | throws `EvaluationLimitException` |
| mark the condition latch | `ConditionTouched` latches true |

**Done when:** build clean under TreatWarningsAsErrors; memo, cycle, depth, latch behave.

---

### Task 2: Condition-evaluator seam (null implementation until m0/06)

- [ ] **Files:** create `src/Custodex.Core/Conditions/IConditionEvaluator.cs`, `…/NullConditionEvaluator.cs`. Test: `tests/Custodex.Core.Tests/Conditions/NullConditionEvaluatorTests.cs`.

**Produces:** `IConditionEvaluator` with `bool Evaluate(ConditionDef, ConditionRef, IReadOnlyDictionary<string, object?> resourceAttributes, RequestContext)`; `NullConditionEvaluator` returning true.
**Consumes (see README):** `ConditionDef`, `ConditionRef`, `RequestContext`.

**Behavior:** the seam lets the authorizer be constructed before the real evaluator exists; `NullConditionEvaluator` treats every condition as satisfied (pure-ReBAC tests). m0/06 ships the real `CelConditionEvaluator : IConditionEvaluator`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `NullConditionEvaluator.Evaluate` on any condition | true |

**Done when:** build clean; null evaluator always satisfies.

---

### Task 3: Schema lookup index

- [ ] **Files:** create `src/Custodex.Core/Evaluation/SchemaIndex.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/SchemaIndexTests.cs`.

**Produces:** `SchemaIndex` wrapping a `Schema` with O(1) ordinal-keyed lookups: `Type` (throws `UnknownTypeException`), `Permission` (throws `UnknownTypeException`/`UnknownPermissionException`), `Relation` (throws `UnknownTypeException`/`UnknownRelationException`), `Condition`, and the no-throw `TryPermission`/`TryRelation`.
**Consumes (see README):** `Schema`, `EntityTypeDef`, `PermissionDef`, `RelationDef`, `ConditionDef`, the `Unknown*Exception` types.

**Behavior:**
- Arrow traversal must ask "does the related type have a permission named X, or only a relation named X?" — `TryPermission` lets the walker fall back from permission to relation (an arrow may target a relation that backs a same-named permission). The index is built per request from the active schema.

**Cases to pin:**

| Setup | Expect |
|---|---|
| resolve a known type/relation/permission | returns the def |
| resolve an unknown type/relation/permission | throws the matching typed exception |
| `TryPermission` on a permission vs a relation | true / false |

**Done when:** build clean; lookups and the permission-vs-relation distinction hold.

---

### Task 4: Subject-set membership — direct, wildcard, nested groups

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/SubjectMembershipTests.cs`.

**Produces:** the `EngineDrivenAuthorizer` constructor `(ISchemaStore, IRelationStore, IAttributeStore, IConditionEvaluator, EvaluationOptions? options = null)`; the relation-membership resolver (`ResolveRelationAsync`); per-tuple condition evaluation (`ConditionSatisfiedAsync`); the per-request `CheckPermissionAsync` walk; and a `CheckAsync` good enough for a bare `RelationRef` permission. The list/batch members are stubbed `NotImplementedException` (m0/07 supplies them in their own partial files).
**Consumes (see README):** `ISchemaStore`, `IRelationStore`, `IAttributeStore`; `EvalContext`, `SchemaIndex`, `IConditionEvaluator`.

**Behavior:**
- Membership is the primitive every operator builds on: does `subject` fill `object#relation` directly, via wildcard `type:*` (matches any subject of that type), or via a nested `group:G#rel` subject-set (recurse one level down)? A tuple's own `Condition`, if present, is evaluated through `IConditionEvaluator`; reaching one latches `ConditionTouched`. A relation cycle on the current path prunes to false; the per-request memo dedupes repeated permission frames (when not explaining).

**Cases to pin** (point Check decisions — each maps to a `ConformanceCase` row in `tests/Custodex.Core.Tests/Conformance/ConformanceCase.cs`):

| Setup | Expect |
|---|---|
| direct user grant on the relation | match for that user, deny for another |
| wildcard `type:*` grant | match for any subject of the type |
| subject-set grant + the subject in that group | match; non-member denied |
| nested groups (group-of-group) to a leaf user | match transitively |
| a membership cycle (a↔b) for an absent user | deny, no throw |

**Done when:** build clean; direct/wildcard/nested/cycle membership behave; list/batch stubs throw `NotImplementedException`.

---

### Task 5: Full algebra — Union, Intersect, Exclude, Arrow, Conditioned

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Expr.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/AlgebraTests.cs`.

**Produces:** `EvalExprAsync` over all six `PermExpr` nodes with short-circuiting; `EvalArrowAsync` (arrow recursion + relation fallback); `BranchConditionSatisfiedAsync` (a `Conditioned` branch-level gate).
**Consumes (see README):** `ResolveRelationAsync`/`CheckPermissionAsync` (Task 4); the `PermExpr` hierarchy.

> **Calibration.** This is the algorithm-heavy heart of the engine: nested intersection/exclusion interleaved with arrow traversal is where a bug hides. Treat the tests here and in m0/09 as the specification; the implementation is the candidate, validated also by the M1 differential harness.

**Behavior** (pointwise operator semantics, spec §7):
- `Union(L, R)` = `S∈L OR S∈R` (short-circuit on first true). `Intersect(L, R)` = `S∈L AND S∈R` (short-circuit on first false). `Exclude(L, R)` = `S∈L AND NOT S∈R` (evaluate L first; skip R if false).
- `Arrow(rel, perm)` = there exists a related object `O'` via `rel` such that `S` holds `perm` on `O'`. Recurse into `O'`'s **full** permission expression — this is why inner exclusions are honoured. If the related type has no permission named `perm` but does have a relation named `perm`, fall back to a relation resolve on `O'`.
- `Conditioned(Inner, name)` contributes only when `Inner` holds **and** the named branch-level condition is satisfied (invoked with empty parameters against request context + the object's synced attributes). Reaching it latches `ConditionTouched`.
- When explaining, both branches of `Union`/`Intersect`/`Exclude` are evaluated so the trace records the full subtree; short-circuit applies only on non-explain checks.

**Cases to pin** (point Check decisions → `ConformanceCase` rows):

| Setup | Expect |
|---|---|
| `viewer + editor`, subject in editor | allow; absent subject deny |
| `vet & trained`, subject in both / only one | allow / deny |
| `viewer - blocked`, blocked subject | deny; unblocked viewer allow |
| `viewer - viewer` (a − a) | deny for everyone |
| `enclosure->edit` inheriting a related-object grant | allow via the related object |
| arrow into a related object whose `edit` has `- blocked` | inner exclusion honoured (denied) |
| arrow whose target is a relation, not a permission | falls back to a relation resolve (allow) |

**Done when:** build clean; the algebra, arrow recursion, inner-exclusion visibility, and relation fallback all hold.

---

### Task 6: The quarantine structural gate (worked example, end to end)

- [ ] **Files:** test `tests/Custodex.Core.Tests/Evaluation/StructuralGateTests.cs` (no new production code).

**Produces:** the discriminating worked example proving the pointwise model on intersection + exclusion + arrow + wildcard together.
**Consumes (see README):** `EngineDrivenAuthorizer` and the full algebra (Task 5).

**Behavior** (spec §12.5): the gate is
`access = (base − enclosure->is_quarantine) + (enclosure->is_quarantine & (vets_or_nurses) & trained)`,
with `is_quarantine` marked by a wildcard `enclosure#is_quarantine@user:*` (a universal set). A top-level post-filter could not see the inner exclusion/intersection; pointwise arrow recursion does. This case is the canary: if it fails, the algebra in Task 5 is wrong — fix Task 5, not the test.

**Cases to pin** (point Check decisions → `ConformanceCase` rows):

| Setup | Expect |
|---|---|
| trained vet inside a quarantine enclosure | allow |
| untrained vet inside quarantine (base revoked) | deny |
| any vet outside quarantine (base intact) | allow |

**Done when:** all three gate outcomes pass.

---

### Task 7: Memoization, Explain tree, diagnostics, and the cacheability seam

- [ ] **Files:** modify `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.cs`; create `src/Custodex.Core/Evaluation/ICacheableAuthorizer.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/CheckObservabilityTests.cs`.

**Produces:** `CheckAsync` wrapped in a `Custodex.check` Activity span (tagging object/permission/subject/allowed/condition-touched) and recording `CustodexDiagnostics.CheckDuration`; `CheckResult.Explain` populated when requested; the internal `ICacheableAuthorizer` interface (`: IAuthorizer`) exposing `internal Task<(bool Allowed, bool ConditionTouched)> CheckInternalAsync(...)`, implemented by `EngineDrivenAuthorizer`. `InternalsVisibleTo("Custodex.Core.Tests")` is added so tests reach the internal seam.
**Consumes (see README):** `CustodexDiagnostics` (m0/01); `EvalContext.ConditionTouched`.

**Behavior:**
- The per-request memo caches `(object, permission, subject) → bool`; when `Explain` is requested the walk bypasses the memo so the trace is the full tree, while non-explain checks keep memoization. The public `CheckResult` cannot signal condition-dependence, so the `ICacheableAuthorizer.CheckInternalAsync` seam returns `(Allowed, ConditionTouched)`; the m0/08 cache uses it to cache only unconditioned results without a public-contract change.

**Cases to pin:**

| Setup | Expect |
|---|---|
| check with / without `Explain` | tree populated / null; root description names the permission |
| any check | one `Custodex.check` Activity emitted |
| any check | `Custodex.check.duration` recorded |
| `CheckInternalAsync` on a condition-free schema | `(allowed, ConditionTouched: false)` |

**Done when:** build clean; span + histogram emitted every check; explain bypasses the memo; the internal seam reports the latch.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] Check is **pointwise** (membership of the query subject), not set materialization — proved by the structural gate (Task 6).
- [ ] A cycle on the DFS path prunes to deny; the depth bound throws `EvaluationLimitException` (Task 1).
- [ ] Arrow recurses into the related object's full permission expression, so inner exclusions are honoured (Task 5).
- [ ] Wildcard `type:*` grants every subject of that type (Task 4).
- [ ] Per-request memo dedupes non-explain sub-checks; explain bypasses the memo (Task 7).
- [ ] One `Custodex.check` Activity per check; `Custodex.check.duration` recorded; `ICacheableAuthorizer.CheckInternalAsync` reports the condition-touched latch (Task 7).
- [ ] The list/batch operations remain `NotImplementedException` for m0/07.
