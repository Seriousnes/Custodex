# M2/04 — Index-Backed ListObjects

**Goal:** Implement an index-backed `ListObjectsAsync` on a new `IndexedAuthorizer` decorator that scans the maintained `reverse_index` (via `IIndexStore` from m2/01) for candidate objects, **re-checks only rows flagged `conditioned`** via the CTE Check, applies the §7.5 over-fetch/refill pagination, and returns results identical to the m1/06 CTE oracle path. The index is the fast path; on a schema-version mismatch the decorator falls back to the inner `NpgsqlCteAuthorizer`, and a parity assertion proves `index ≡ oracle`.

**For implementers:** drive this with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each `### Task` is one TDD unit — Red → Green → one Conventional-Commit with the co-author trailer (see `../README.md` → Global Constraints). Tasks are tracked with `- [x]` checkboxes.

**Architecture/approach:** `IndexedAuthorizer : IAuthorizer` wraps the inner `NpgsqlCteAuthorizer` (m1/05/m1/06) and adds an `IIndexStore`. For `ListObjectsAsync` it scans `reverse_index` rows for `(store, tenant, schema_version, subject, permission, object_type)` — already-resolved structural grants. Unconditioned rows return directly (the maintained index already proved the structural grant holds). Rows flagged `conditioned` are re-checked with the full pointwise CTE Check (which honours tuple/branch conditions against synced attributes + request context) and dropped if the condition fails. If the index holds no rows for the active schema version (a schema change invalidated it before a rebuild ran), the decorator falls back to the inner authorizer so a stale or not-yet-rebuilt index never serves wrong answers. Pagination is over-fetch and refill (spec §7.5), reusing `ContinuationCursor` from `Custodex.Core.Evaluation`. `ListSubjectsAsync`, `CheckAsync`, and `BatchCheckAsync` delegate to the inner authorizer (the reverse index is keyed for the subject→objects direction; ListSubjects stays on the CTE path).

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, Testcontainers.PostgreSql. Reuses `Custodex.Core` (`ContinuationCursor`) and `Custodex.Storage.Postgres` (`NpgsqlCteAuthorizer`, `NpgsqlIndexStore`, `MigrationRunner`, `PostgresFixture`, `NpgsqlUnitOfWorkFactory`, the relation/schema/attribute stores).

**Global Constraints:** see `../README.md` → Global Constraints, and its Calibration note.

**Dependencies:** builds on m0/01 (`IIndexStore`, `IAuthorizer`); m0/07 (the oracle's ListObjects semantics, `ContinuationCursor`); m1/01 (`reverse_index`, `MigrationRunner`, `PostgresFixture`); m1/05 (`NpgsqlCteAuthorizer` Check); m1/06 (the CTE `ListObjectsAsync` this must match and fall back to); m2/01 (`IIndexStore` scan + has-rows-for-version, the canonical subject-string encoding, `NpgsqlIndexStore` — see README Post-dispatch reconciliations §4 for the member contract); m2/02/m2/03 (index population — the rebuild and incremental maintenance that fill `reverse_index`).

**Pagination contract (spec §7.5).** Conditioned candidates are re-checked and may be dropped after the indexed scan, so storage-level paging alone yields unpredictable page sizes. The contract is over-fetch and refill: scan candidates in a deterministic order (ordinal by object id), re-check conditioned rows, return exactly `PageSize` confirmed ids (or fewer only at the true end). The returned token is the opaque `ContinuationCursor`; a null token means the end of results. This is byte-for-byte the m0/07/m1/06 contract.

> **Calibration (critical).** The index-backed path is trusted only where it agrees with the oracle. The **tests are the spec**: every test pins behaviour the m1/06 CTE `ListObjectsAsync` (itself proven against the m0/07 oracle) produces. The m2/06 differential harness (`index ≡ oracle` including post-write maintenance) is the only proof the fast path is correct; if it diverges, the index path is wrong and the oracle is right. The fallback-on-version-mismatch is the safety net that makes serving from the index safe before the rebuild lands.

---

### Task 1: `IndexSubject` — canonical subject-string encoding

- [x] **Files:** create `src/Custodex.Storage.Postgres/IndexSubject.cs`; test `…Tests/Index/IndexSubjectTests.cs`.

**Produces:** `static string IndexSubject.Of(SubjectRef)` — the canonical `reverse_index.subject` string: `type:id`, `type:id#relation` for a subject-set, `type:*` for a wildcard.
**Consumes (see README):** `SubjectRef` (`Custodex.Abstractions`).

**Behavior:** index maintenance (m2/02/m2/03) and index reads (this plan) must agree on the subject string byte-for-byte, or a scan misses the rows maintenance wrote. Centralizing the encoding in one helper removes drift; it mirrors the canonical `SubjectRef.ToString()` encoding m2/01 stores. If m2/01 ships its own helper of the same shape, delete this copy and reference theirs (report as a contract gap).

**Cases to pin** (each maps to a single encoder assertion):

| Input | Expect |
|---|---|
| `("user","alice")` | `user:alice` |
| `("group","vets","member")` | `group:vets#member` |
| `("user","*")` | `user:*` |

**Done when:** build clean; cases pass.

---

### Task 2: `IndexedAuthorizer` — delegate everything, stub ListObjects

- [x] **Files:** create `src/Custodex.Storage.Postgres/IndexedAuthorizer.cs`; test `…Tests/Index/IndexedAuthorizerDelegationTests.cs`.

**Produces:** `IndexedAuthorizer(NpgsqlCteAuthorizer inner, IIndexStore index, ISchemaStore schemaStore) : IAuthorizer` (a `partial` class). This task wires `CheckAsync`/`BatchCheckAsync`/`ListSubjectsAsync` straight through to `inner`, and stubs `ListObjectsAsync` to delegate to `inner` (replaced in Task 3). The schema store supplies the active schema version that keys the index scan.
**Consumes (see README):** `NpgsqlCteAuthorizer` (m1/05/m1/06), `IIndexStore` + `NpgsqlIndexStore` (m2/01), `ISchemaStore` (m0/01).

**Behavior:** wrapping the concrete `NpgsqlCteAuthorizer` (not the interface) documents that the fallback and parity oracle is the proven CTE path; Check/BatchCheck/ListSubjects are unaffected by the reverse index and pass through unchanged. `NpgsqlIndexStore` is the m2/01 `IIndexStore` implementation used as a collaborator here.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `viewer@alice` on `doc:D1`, schema active, no `reverse_index` rows; Check D1/view/alice | allowed — delegates to inner CTE Check |
| `viewer@alice` + `viewer@bob` on D1; ListSubjects D1/view | `[alice, bob]` — delegates to inner CTE ListSubjects |

**Done when:** build clean; cases pass; Check/BatchCheck/ListSubjects delegate unchanged; requires Docker.

---

### Task 3: Index-backed `ListObjectsAsync` — scan, re-check conditioned, refill, fall back

- [x] **Files:** create `src/Custodex.Storage.Postgres/IndexedAuthorizer.ListObjects.cs`; test `…Tests/Index/IndexedListObjectsTests.cs`.

**Produces:** the real `ListObjectsAsync` on `IndexedAuthorizer`, replacing the Task-2 pass-through.
**Consumes (see README):** `IIndexStore` scan + has-rows-for-version; `ContinuationCursor` (`Custodex.Core.Evaluation`); inner `NpgsqlCteAuthorizer.CheckAsync`/`ListObjectsAsync`; `IndexSubject.Of` (Task 1).

**Behavior** (spec §7.3, §7.5):
- Read the active schema version. If the index holds no rows for it, delegate to the inner CTE `ListObjectsAsync` — a stale or not-yet-rebuilt index must never serve answers.
- Otherwise scan candidates in ordinal object-id order after the decoded cursor. Return unconditioned rows directly; re-check only `conditioned` rows via the inner Check and drop those whose condition fails. (An unconditioned row is the index asserting a resolved structural grant — already true. A `conditioned` row is gated by a request-time predicate the index cannot evaluate, so it is stored-but-flagged and never assumed — this is the §7.3 M2 contract: "one indexed scan plus a condition re-check only on rows flagged `conditioned`.")
- Over-fetch and refill to exactly `PageSize` confirmed ids (fewer only at the true end). The cursor encodes the last scanned id, not the last confirmed — so resumption never re-scans dropped rows; emit a token only when a further candidate confirms.

**Cases to pin:**

| Setup | Expect |
|---|---|
| two unconditioned rows | both, sorted; null token |
| one unconditioned + one conditioned that fails re-check (a `blocked` exclusion) | only the unconditioned id |
| no index rows for active version | identical to inner CTE `ListObjectsAsync` |
| 5 unconditioned grants, pageSize 2 | `[a,b]`,`[c,d]`,`[e]`; resumable; null token last |
| 5 grants, 2 conditioned drop mid-page (b,d blocked), pageSize 2 | `[a,c]` then `[e]` — refill holds page size |

**Done when:** build clean; cases pass; parity with inner CTE `ListObjectsAsync` (Task 4 + m2/06 differential harness); requires Docker.

---

### Task 4: Parity anchor — `IndexedAuthorizer ≡ NpgsqlCteAuthorizer` for ListObjects

- [x] **Files:** test only `…Tests/Index/IndexedListObjectsParityTests.cs` — no new production code.

**Produces:** a focused, always-on parity test proving the index-backed `ListObjectsAsync` returns the same ids (and the same paged ranges) as the inner CTE `ListObjectsAsync` oracle across exclusion, wildcard, and conditioned-drop shapes — the per-boundary complement to m2/06's property harness (fast feedback if a refactor breaks parity, without the full property run).
**Consumes (see README):** `IndexedAuthorizer` (Tasks 2–3), `NpgsqlCteAuthorizer` (m1/06), `NpgsqlIndexStore` (m2/01).

**Behavior — the invariant being asserted:** seed index rows to mirror what m2/02/m2/03 maintenance would produce (a structural row per grant path, `conditioned=true` only where a request-time predicate gates the branch), then page both the indexed path and the CTE oracle to exhaustion and assert equal id sequences. A `group:macropods#member` subject row models a grant the subject reaches through membership; index and oracle resolve the membership identically.

**Cases to pin:**

| Setup | Expect |
|---|---|
| kangaroo+wallaby via `group:macropods#member`; `blocked@alice` on wallaby; quokka `editor@alice`; alice in macropods; index rows: kangaroo (uncond), wallaby (conditioned), quokka (uncond); page both at pageSize 2 | indexed == oracle == `[kangaroo, quokka]` (wallaby dropped by the re-check) |

**Done when:** build clean; indexed paging equals the CTE oracle end to end; requires Docker.

---

## Self-review checklist (after all tasks)

- [x] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [x] `IndexSubject.Of` produces the exact `reverse_index.subject` string maintenance writes (Task 1).
- [x] Check/BatchCheck/ListSubjects delegate unchanged to the inner CTE authorizer (Task 2).
- [x] Unconditioned index rows return directly; only `conditioned` rows are re-checked via the inner Check (Task 3).
- [x] An empty index for the active schema version falls back to the inner CTE `ListObjectsAsync` (Task 3).
- [x] Pagination returns exactly `PageSize` confirmed ids except at the true end, even when conditioned rows drop inside a page; the cursor encodes the last scanned id and is the m0/07 `ContinuationCursor` (Tasks 3–4).
- [x] The index-backed path equals the CTE oracle for exclusion/wildcard/conditioned shapes (Task 4).

## Contract gaps (reported, not changed)

- **`IIndexStore` scan/has-rows-for-version members are defined by m2/01.** This plan consumes the index scan and the has-rows-for-version check semantically (README Post-dispatch reconciliations §4 is their canonical home; m2/01 realizes them). If m2/01 lands a different scan signature or subject encoding, adapt this plan's calls and `IndexSubject` to match. No README edit by this plan.
- **Staleness primitive: is-built marker vs has-rows-for-version (interlock to resolve).** m2/01 defines the staleness check as an **is-built marker** (backed by `index_build_markers`; written by m2/02's mark-built, read by m2/03's skip-logic), whereas this plan's fallback triggers when **the index holds no rows for the active version** — a row-presence check, which differs (a built-but-empty tenant has a marker and zero rows). The implementer must resolve which primitive m2/04's fallback rides: the m2/01 is-built marker, or a separate has-rows member m2/01 owes. Reported for the maintainer; no README edit by this plan.
- **The conditioned re-check uses the public `CheckAsync`, not an internal `(Allowed, ConditionTouched)` path.** For ListObjects the decorator only needs allow/deny per conditioned candidate, which `CheckAsync` provides; it does not need the cacheability signal m0/08 consumes. No new contract surface required.
