# M1/06 — CTE ListObjects, ListSubjects & Pagination

**Goal:** Implement `NpgsqlCteAuthorizer.ListObjectsAsync` and `ListSubjectsAsync` (spec §7.3 Milestone 1 / §7.5) over the recursive-CTE path, with the over-fetch/refill pagination contract and the same `ContinuationCursor` shape M0/07 uses (reused from `Custodex.Core.Evaluation`). Candidate generation runs in SQL; each candidate is confirmed by the pointwise CTE Check from M1/05, so exclusion/intersection/conditions are honoured — identical results to the M0/07 oracle.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach (the M1/02 seam, reverse direction):** the recursive CTE computes a candidate **superset**; correctness comes from re-confirming each candidate with the full pointwise `CheckPermissionAsync` from M1/05. `ListObjects` is the oracle's Milestone-1 strategy (spec §7.3) ported to Postgres: SQL gathers candidates fast, C# confirms them correctly. `ListSubjects` forward-collects candidate leaf users (relations, nested groups, arrow targets) via the reachability CTE + a recursive C# walk, then confirms each with Check. Pagination is over-fetch and refill (spec §7.5): scan candidates in ordinal-id order, confirm, return exactly `PageSize` (or fewer only at the true end) with an opaque `ContinuationCursor` encoding the last-confirmed id; a null token means end of results. This is the M0/07 contract — sets, not order, are the comparison basis.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Reuses `Custodex.Core` (`ContinuationCursor`, `EvalContext`, `EvalFrame`, `SchemaIndex`).

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on `m0/01` (Abstractions), `m0/07` (`ContinuationCursor` shape + the oracle's list semantics this must match — see README), M1/01–M1/04 (schema, UoW, stores), M1/05 (`NpgsqlCteAuthorizer`, `CheckPermissionAsync`, `CteReachability`).

> **Calibration.** The candidate-generation CTEs are the hardest SQL after the check path. The **tests are the spec**. Candidate generation must never miss a true positive (the confirm-by-Check step removes false positives, never adds them); if M1/08 finds a missed candidate, widen the CTE — the test is right.

---

### Task 1: Reverse-reachability candidate CTE + the type universe

- [ ] **Files:** create `src/Custodex.Storage.Postgres/CteCandidates.cs`; test `…Tests/Cte/CteCandidatesTests.cs`.

**Produces:** `CteCandidates` with two static methods:
- `ReachableObjectIdsAsync` — the distinct, **ordinal-sorted** ids of objects of a type reverse-reachable from a subject (a complete superset of the ListObjects answer; each confirmed later by Check).
- `TypeUniverseAsync` — all object ids of a type appearing as an object in any tuple (covers `type:*` wildcard grants, which reverse traversal does not reach from a concrete subject).

**Consumes (see README):** the `relation_tuples` schema (M1/01); `SubjectRef`/`TenantContext`.

**Behavior:** the reverse-reachability CTE climbs the subject → ancestors graph: the base principal is the subject, and the recursive step follows inbound tuples upward by treating **any object whose tuple subject matches a current principal as the next principal** (using `rt.relation` as the climbed principal's relation, and matching `COALESCE(subject_relation,'')`) — a *general* subject-set climb, not restricted to `group#member`, so non-group subject-set nesting (e.g. `team#owner`) is also followed. From every reached principal it collects the objects it appears on; a second recursive arm follows structural edges transitively (objects whose null-subject-relation tuples point at an already-reached object). All results are distinct, ordinal-sorted (so cursors are stable) and hard-filter `store + tenant`. The type universe is a simple distinct-and-sorted select.

**Cases to pin:**

| Setup | Expect |
|---|---|
| grants via a nested group the subject belongs to | the reachable object ids, sorted, excluding objects granted only to others |
| any tuples of a type | the full sorted set of that type's object ids |

**Done when:** build clean; both cases pass (Postgres required); also exercised by the M1/08 harness.

---

### Task 2: `ListObjectsAsync` — over-fetch, confirm, refill, paginate

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.ListObjects.cs` (remove the M1/05 stub); test `…Tests/Cte/CteListObjectsTests.cs`.

**Produces:** `ListObjectsAsync` — builds the candidate set (`ReachableObjectIdsAsync ∪ TypeUniverseAsync`), keeps it ordinal-sorted and distinct, skips strictly after the decoded cursor, confirms each via the full pointwise `CheckPermissionAsync` (fresh `EvalContext` per candidate), and returns exactly `PageSize` confirmed ids with a resumable `ContinuationCursor`. One open connection serves candidate generation and all confirms.
**Consumes (see README):** `CteCandidates` (Task 1); `CheckPermissionAsync`/`EvalContext`/`SchemaIndex` (M1/05); `ContinuationCursor` (`Custodex.Core.Evaluation`); `ListObjectsRequest`/`ListObjectsResult`.

**Behavior** (spec §7.3, §7.5): validates the request type/permission first (throws `Unknown*Exception` on bad input). The cursor encodes the last *confirmed* id; a token is emitted only when a further candidate confirms beyond the page, so resumption never re-serves or drops.

**Cases to pin:**

| Setup | Expect |
|---|---|
| two objects granted via a group, one of them also blocked for the subject | only the unblocked id; null token |
| three objects granted via `editor@user:*` | every object of the type, sorted |
| five wildcard grants, pageSize 2 | `[a,b]`, `[c,d]`, `[e]`; resumable; null token last |
| drain all pages over a small range, pageSize 2 | union equals the full set, no overlaps or gaps |

**Done when:** build clean; all four cases pass (Postgres required); parity with the M0/07 oracle, asserted by M1/08.

---

### Task 3: `ListSubjectsAsync` — forward-collect leaf users, confirm, paginate

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.ListSubjects.cs` (remove the M1/05 stub); test `…Tests/Cte/CteListSubjectsTests.cs`.

**Produces:** `ListSubjectsAsync` — forward-collects candidate leaf `user`s from the whole permission expansion (relations, nested groups, arrow targets) via a cycle-guarded recursive C# walk over the reachability edges, records whether a `user:*` wildcard appears, then confirms each candidate (including the surfaced `"*"` subject) with the pointwise Check, returning them ordinal-sorted with the same over-fetch / `ContinuationCursor` contract.
**Consumes (see README):** `CteReachability.SubjectsThroughRelationAsync`/`EdgesThroughRelationAsync` (M1/05); `CheckPermissionAsync`; `ContinuationCursor`; `EvalFrame`; `ListSubjectsRequest`/`ListSubjectsResult`.

**Behavior** (spec §7.5): a `user:*` in any contributing relation surfaces the special `"*"` subject so a public grant is visible; `"*"` (0x2A) sorts first ordinal and flows through the same confirm + paginate loop (no bonus row past `PageSize`). The collector is cycle-guarded by a visited-frame set so nested-group loops terminate.

**Cases to pin:**

| Setup | Expect |
|---|---|
| leaf users via nested groups, one of them blocked | only the unblocked user |
| three members via a group, pageSize 2 | `[a,b]` then `[c]`; resumable; null token last |
| wildcard `viewer@user:*` plus two direct users, pageSize 2 | `[*, a]` then `[b]`; `"*"` sorts first, page size respected |
| a nested-group membership cycle | does not overflow; returns the reachable subjects |

**Done when:** build clean; all four cases pass (Postgres required); `NpgsqlCteAuthorizer` now implements every `IAuthorizer` member; set-equivalence with the oracle asserted by M1/08.

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors.
- [ ] Reverse-reachability climbs the general subject-set graph (any relation, not just `group#member`); the type universe covers wildcard grants; candidates are sorted, distinct (Task 1).
- [ ] `ListObjects` confirms each candidate via the pointwise Check — exclusion/intersection/conditions honoured; matches the oracle (Task 2).
- [ ] Pagination returns exactly `PageSize` confirmed ids except at the true end; the cursor is the M0/07 `ContinuationCursor`; no dupes, no gaps across a full drain (Tasks 2–3).
- [ ] `ListSubjects` expands nested groups + arrow targets to leaf users, confirms each, surfaces an unexcluded `user:*` as `"*"` within `PageSize`, and is cycle-safe (Task 3).
- [ ] Both operations validate the request type/permission (throw `Unknown*Exception` on bad input).
