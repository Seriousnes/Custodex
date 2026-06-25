# M0/07 — Engine-Driven ListObjects, ListSubjects & BatchCheck

**Goal:** Extend `EngineDrivenAuthorizer` (m0/05) with engine-driven `ListObjectsAsync` (the correctness oracle), `ListSubjectsAsync`, and `BatchCheckAsync`, plus the over-fetch/refill pagination contract with an opaque, deterministic continuation cursor.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** `ListObjects` is the **oracle**: build a candidate superset (objects the subject is reverse-reachable to, ∪ the type universe so wildcard grants aren't missed), then **confirm each candidate with the same pointwise Check** that m0/05 proved — including conditions. It is "always correct, heavier when access is broad" (spec §7.3). `ListSubjects` forward-expands the permission tree down to leaf `user`s, then confirms each. `BatchCheck` runs many items sharing one per-request memo. Pagination orders candidates by object id (ordinal) so an opaque cursor can encode the last-confirmed id and resume. Each operation is its own partial file on `EngineDrivenAuthorizer`.

**Pagination contract (spec §7.5):** conditioned candidates re-checked after the scan may be dropped, so storage-level paging alone yields unpredictable page sizes. The contract is **over-fetch and refill**: scan candidates in a deterministic order, confirm the permission (and conditions) per candidate, and return exactly `PageSize` confirmed ids (fewer only at the true end). The `ContinuationToken` is an opaque cursor encoding the last-confirmed id; a null token means the end.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly. Tests use `Custodex.Storage.InMemory` (m0/04).

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/05 (`EngineDrivenAuthorizer`, `CheckPermissionAsync`, `ResolveRelationAsync`, `EvalContext`, `SchemaIndex`) — see README. Uses `IRelationStore.ListObjectIdsAsync` for the type universe.

---

### Task 1: Deterministic candidate enumeration — reverse reachability

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Reverse.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/ReverseReachabilityTests.cs`.

**Produces:** a private `CandidateObjectsAsync(TenantContext, SubjectRef, string objectType, CancellationToken)` returning the distinct, ordinal-id-sorted objects of `objectType` reverse-reachable from the subject; an `internal CandidateObjectsForTest` seam exposing it to the test assembly.
**Consumes (see README):** `IRelationStore.GetBySubjectAsync`, `SchemaIndex`, `EntityRef`, `SubjectRef`.

**Behavior:**
- Reverse traversal cheaply gathers "objects this subject is plausibly connected to": the subject's inbound tuples, plus the tuples of every group it transitively belongs to (climb subject → groups → groups-of-groups), plus objects reachable by following structural-reference edges. It is a **superset** — it does not itself respect intersection/exclusion; correctness comes from confirming each candidate with the full Check in Task 3. The traversal is cycle-guarded; the result is distinct and ordinal-id-sorted so pagination cursors are stable.

> **Calibration.** Candidate generation must never miss a true positive (the confirm-by-Check step removes false positives). If a worked example or the M1 differential harness finds a missed candidate, widen this traversal — the tests are the spec, the BFS is the candidate-completeness approach to validate.

**Cases to pin:**

| Setup | Expect |
|---|---|
| objects granted via direct and nested-group editor relations | the two granted ids, sorted; objects granted to others excluded |
| a cyclic group-membership graph | candidate enumeration terminates and lists the reachable object once |

**Done when:** build clean under TreatWarningsAsErrors; candidate set is complete, distinct, sorted, cycle-safe.

---

### Task 2: Pagination cursor

- [ ] **Files:** create `src/Custodex.Core/Evaluation/ContinuationCursor.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/ContinuationCursorTests.cs`.

**Produces:** `ContinuationCursor` with `static string Encode(string lastObjectId)` and `static string? DecodeAfter(string? token)`.
**Consumes (see README):** nothing beyond the BCL.

**Behavior:** an opaque, deterministic cursor over the stable object-id ordering — Base64Url of the last-returned id. `DecodeAfter` returns the id to resume strictly after, or null for "from the start" (null/empty token).

**Cases to pin:**

| Setup | Expect |
|---|---|
| `Encode("…")` then `DecodeAfter` | round-trips the id; the token is opaque (not the raw id) |
| `DecodeAfter(null)` / `DecodeAfter("")` | null (start) |

**Done when:** build clean; round-trip and null semantics hold.

---

### Task 3: `ListObjectsAsync` — over-fetch, confirm, refill, paginate

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListObjects.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/ListObjectsTests.cs`.

**Produces:** `ListObjectsAsync` (replacing the m0/05 stub) confirming each candidate via the pointwise Check and honouring the over-fetch/refill contract; a private `AnyConfirmedAfterAsync` look-ahead deciding whether to emit a token.
**Consumes (see README):** `CandidateObjectsAsync` (Task 1), `ContinuationCursor` (Task 2), `CheckPermissionAsync`/`EvalContext` (m0/05), `IRelationStore.ListObjectIdsAsync` (the type universe — covers wildcard grants).

**Behavior:**
- Validate the request type/permission against the schema (throws on unknown). Build the candidate set: reverse-reachable ∪ the type universe (`ListObjectIdsAsync`), sorted distinct by ordinal id. Skip to strictly after the decoded cursor. Walk in order; for each candidate run the full pointwise Check under a fresh `EvalContext` (which evaluates conditions); keep confirmed ids. Stop once `PageSize` are confirmed; emit a token (the last-confirmed id) only when a look-ahead finds a further confirmable candidate — null at the true end. Each candidate is confirmed independently, so a page is exactly `PageSize` unless candidates are exhausted, and pages never overlap or drop ids across the full range.

**Cases to pin:**

| Setup | Expect |
|---|---|
| two granted, one revoked by `- blocked` | only the unrevoked id; null token |
| wildcard `user:*` grants on three objects | all three, sorted |
| 5 wildcard grants, pageSize 2 | `[a,b]`, `[c,d]`, `[e]`; resumable; null token last |
| page the full range with pageSize 2 | union equals all ids, no dupes, no gaps |

**Done when:** build clean; exclusion honoured, wildcards surfaced, pagination exact and resumable.

---

### Task 4: `ListSubjectsAsync` — forward-expand to leaf users

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListSubjects.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/ListSubjectsTests.cs`.

**Produces:** `ListSubjectsAsync` (replacing the m0/05 stub) forward-expanding the permission tree to leaf `user` subjects, confirming each with Check, paginated like ListObjects; the private collectors (`CollectLeafSubjectsAsync`/`CollectFromExprAsync`/`CollectFromRelationAsync`) and `AnySubjectConfirmedAfterAsync`.
**Consumes (see README):** `SchemaIndex`, the permission AST walk, `IRelationStore.GetByObjectAsync`, `CheckPermissionAsync`, `ContinuationCursor`.

**Behavior:**
- Forward-collect every `user` appearing anywhere in the permission's expansion (relations, nested groups, arrow targets) — a candidate superset — then **confirm each with Check**, so exclusions/intersections are honoured. A `user:*` wildcard in any contributing relation means "every user"; for the oracle, surface the discovered concrete users plus, when an unexcluded wildcard is present, the special `"*"` subject so callers can detect a public grant. `"*"` sorts first ordinal and flows through the same confirm + paginate loop (no special-casing, exact page sizes). Confirmed subjects are id-sorted; pagination uses the cursor.

**Cases to pin:**

| Setup | Expect |
|---|---|
| nested-group viewers with one `- blocked` user | leaf users minus the blocked one |
| 3 leaf users, pageSize 2 | `[a,b]` then `[c]`; resumable |
| a `viewer@user:*` public grant alongside concrete users, pageSize 2 | `"*"` first, exactly `PageSize` per page (no bonus row) |

**Done when:** build clean; nested groups expand, exclusion honoured, wildcard surfaced without overflowing a page.

---

### Task 5: `BatchCheckAsync` — shared per-request memo

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Batch.cs`. Test: `tests/Custodex.Core.Tests/Evaluation/BatchCheckTests.cs`.

**Produces:** `BatchCheckAsync` (replacing the m0/05 stub) evaluating every `CheckItem` against one shared `EvalContext`, returning a `CheckResult` per item in request order.
**Consumes (see README):** `CheckPermissionAsync`, `EvalContext`, `SchemaIndex`.

**Behavior:** a batch typically asks many `(object, perm)` for the same subject, or many subjects over overlapping nested groups; sharing one memo across items collapses the redundant nested-group expansions. Items are decision-only (no explain) and independent allow/deny.

**Cases to pin:**

| Setup | Expect |
|---|---|
| three items (via-group allow, deny, direct allow) | results `[true, false, true]` in request order |
| empty batch | empty result list |

**Done when:** build clean; results in order; one shared memo; full Evaluation suite green.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] `ListObjects` is the oracle: candidate superset (reverse-reachable ∪ `ListObjectIdsAsync` type universe) confirmed by the pointwise Check — exclusion/intersection honoured.
- [ ] Pagination returns exactly `PageSize` confirmed ids except at the true end; the cursor is opaque, deterministic, resumable; no dupes, no gaps.
- [ ] Wildcard `type:*` grants surface every object of the type in `ListObjects`.
- [ ] `ListSubjects` expands nested groups and arrow targets to leaf users, confirms each, and surfaces an unexcluded `user:*` public grant.
- [ ] `BatchCheck` shares one `EvalContext` memo across items and returns results in request order.
- [ ] All three operations validate the request type/permission (throw `Unknown*Exception` on bad input).
