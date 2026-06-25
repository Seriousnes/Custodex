# M1/02 — CTE Nested-Algebra Spike

**Goal:** A small, throwaway spike that proves how much of the permission algebra a recursive Postgres CTE can absorb when nested exclusion / intersection interleave *with* arrow traversal (spec §7.1), and decides the CTE/engine seam that M1/05 (CTE Check) and M1/06 (CTE ListObjects) build on.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach:** The spike lives entirely in the test project under `tests/Custodex.Storage.Postgres.Tests/Spike/` and is kept as documentation once the decision is recorded. It seeds two hand-checked datasets into the real `relation_tuples` schema (M1/01), runs a candidate recursive CTE that expands **reachability only** (nested subject-set membership + arrow edge-following), and demonstrates on a discriminating case that a naive **all-in-SQL top-level post-filter** returns the wrong answer while the conservative seam — **CTE for reachability, C# for the boolean algebra** — returns the answer the M0/05 `EngineDrivenAuthorizer` oracle gives. That divergence is the bug the M1/08 differential harness exists to catch, demonstrated here on a single case.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on M1/01 (`relation_tuples` schema, `MigrationRunner`, `PostgresFixture`). The hand-computed truth must match what `EngineDrivenAuthorizer` (M0/05) returns for the same schema + tuples — that pointwise oracle is the spike's reference.

## The decided seam (this plan's primary output)

This box is the artefact M1/05, M1/06, and M1/08 reference. It is recorded in `tests/Custodex.Storage.Postgres.Tests/Spike/SEAM_DECISION.md`.

> **Custodex CTE/engine seam.** The recursive Postgres CTE computes **reachability only**: from an `(object, relation)` it expands nested subject-set membership (`group:G#member` → `G`'s `member` tuples, transitively) and follows structural-reference edges for arrows (`object#rel@related:id` → the related object), producing the leaf set of `(subject_type, subject_id)` rows reachable through one relation or one arrow hop. It does **not** compute union/intersection/exclusion/conditioned across branches and does **not** post-filter at the top level.
>
> The **boolean algebra is composed in C#** in `NpgsqlCteAuthorizer`, walking the `PermExpr` tree exactly as `EngineDrivenAuthorizer` does (M0/05): the operators short-circuit over sub-results; `Arrow(rel, perm)` follows the `rel` edges (one CTE reachability query) and recurses into each related object's **full** `perm` expression — so an inner exclusion under an arrow is always seen, because the C# walk re-enters the related object's whole expression rather than post-filtering the top object. Conditions are evaluated in C# via `IConditionEvaluator`. `NpgsqlCteAuthorizer` is a Postgres-native reimplementation of the oracle's traversal whose only divergence is *where the recursion runs* (SQL vs C# loops); `NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer` is the correctness claim, and the M1/08 harness is its proof.
>
> **Project reference:** `Custodex.Storage.Postgres` references `Custodex.Core` (for `SchemaIndex`, `EvaluationOptions`, `EvalContext`/`EvalFrame`, `ContinuationCursor`, `IConditionEvaluator`). Spec §4 forbids DB code *in Core*, not Core being referenced by a provider. Core never references Postgres. (README post-dispatch reconciliation 5.)

---

### Task 1: The throwaway schemas and the discriminating tuple sets

- [ ] **Files:** create `…Tests/Spike/SpikeData.cs`; test `…Tests/Spike/SpikeTruthTableTests.cs`.

**Produces:** `SpikeData` holding the concrete tuples and hand-computed expected answers for two cases, plus a pure (no-DB) test pinning the truth table so the SQL tasks have a fixed target.

**Behavior:** two cases interleave algebra *with* arrow traversal (the spec §7.1 hard shape). Identifiers are domain-neutral (the project's `tests/` tree is vocabulary-guarded).
- **Case A — inner exclusion under an arrow (the discriminator):** an outer type's permission arrows into a related type whose permission is `editor - blocked`. One subject is both editor and blocked on the related object. Hand truth: that subject → **deny** (inner exclusion revokes through the arrow); an editor-not-blocked subject → **allow**. A naive CTE that gathers editors reachable through the arrow and post-filters `blocked` *on the outer object* sees no such tuple and wrongly allows the blocked subject.
- **Case B — intersection through arrow with a wildcard gate (spec §12.5):** `access = arrow->is_quarantine & member_a & member_b`, with `is_quarantine` filled by `user:*`. Hand truth: subject in both groups → allow; subject in only one → deny; non-member → deny.

**Cases to pin:**

| Setup | Expect |
|---|---|
| Case A truth table | editor-and-blocked subject → false; editor-only subject → true |
| Case B truth table | both-groups subject → true; one-group subject → false; non-member → false |

**Done when:** build clean; both truth-table cases pass (no Postgres).

---

### Task 2: The reachability CTE — one relation expands nested subject-sets

- [ ] **Files:** create `…Tests/Spike/ReachabilityCte.cs`, `…Tests/Spike/SpikeSeed.cs`; test `…Tests/Spike/ReachabilityCteTests.cs`.

**Produces:** `ReachabilityCte.SubjectsThroughRelationAsync(...)` — the candidate recursive CTE that, given one `(object, relation)`, returns the distinct **leaf** subjects (concrete users and wildcards) reachable by transitively expanding subject-set tuples. `SpikeSeed` writes raw rows via Dapper (the relation store arrives in M1/04).
**Consumes (see README):** the `relation_tuples` schema (M1/01).

**Behavior:** a recursive CTE whose base row is the requested `(object_type, object_id, relation)` and whose recursive step, for any frontier tuple whose subject is a subject-set, joins back to pull that subject-set's tuples; terminal rows (subjects with null subject-relation, including wildcards) are the leaf set. `UNION` (not `UNION ALL`) deduplicates the frontier, so a membership cycle terminates — cycle-safety without an explicit cycle clause for this shape.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `obj#rel@group:G#member`, `group:G#member@{userX,userY}` | leaves `{userX, userY}`; the subject-set itself is not returned |
| `obj#rel@user:*` | the wildcard `user:*` surfaces as a leaf |

**Done when:** build clean; both cases pass (Postgres required).

---

### Task 3: The discriminator — naive all-in-SQL is wrong, the seam is right (Case A)

- [ ] **Files:** create `…Tests/Spike/SeamVsNaiveTests.cs` (no new production code).

**Consumes (see README):** `ReachabilityCte`, `SpikeData`/`SpikeSeed`.

**Behavior:** the heart of the spike. On Case A, a naive top-level post-filter CTE ("expand everyone reachable through the arrow→editor, then subtract anyone in the outer object's `blocked`") returns the wrong answer — there is no `blocked` tuple on the outer object, so the blocked subject survives. The decided seam instead asks the CTE only "who is reachable through the related object's `editor`" and "…`blocked`", then evaluates `editor - blocked` pointwise in C# on the related object — correct. A minimal in-test C# walk for Case A demonstrates the seam; it is the shape M1/05 generalizes.

**Cases to pin:**

| Setup | Expect |
|---|---|
| naive all-in-SQL, blocked subject | returns **true** (documents the bug) |
| decided seam, Case A | matches the hand truth: blocked subject → false, editor-only subject → true |

**Done when:** build clean; both cases pass (Postgres required); the naive-wrong case is asserted as wrong to capture the divergence.

---

### Task 4: The seam handles intersection-through-arrow with a wildcard gate (Case B)

- [ ] **Files:** create `…Tests/Spike/SeamIntersectionGateTests.cs` (no new production code).

**Consumes (see README):** `ReachabilityCte`, `SpikeData`/`SpikeSeed`.

**Behavior:** proves the seam returns the §12.5 truth for `access = arrow->is_quarantine & member_a & member_b`. Wildcard handling mirrors the oracle: a relation resolves true for subject `S` when its reachable leaves contain `(user, S)` **or** `(user, *)`, so the universal `is_quarantine@user:*` makes the arrow true for any user and the intersection's truth is decided by the two memberships. A `[Theory]` over three subjects pins the result.

**Cases to pin:**

| Setup | Expect |
|---|---|
| both-groups subject, quarantine universal | true |
| one-group subject | false |
| non-member | false |

**Done when:** build clean; the three theory cases pass (Postgres required).

---

### Task 5: Record the decision

- [ ] **Files:** create `…Tests/Spike/SEAM_DECISION.md`.

**Produces:** the seam document M1/05/06/08 point at: (1) reachability is CTE-expressible and cycle-safe; (2) the boolean algebra must stay in C# — a top-level SQL post-filter is provably wrong on inner exclusion through an arrow; (3) `Custodex.Storage.Postgres → Custodex.Core` reference. Reproduces the boxed seam paragraph above.

**Done when:** the note is committed and matches the decided seam.

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors.
- [ ] The two hand-computed truth tables are pinned and match what `EngineDrivenAuthorizer` returns (Task 1).
- [ ] The reachability CTE expands nested subject-sets to leaf users, surfaces the wildcard leaf, and is cycle-safe (Task 2).
- [ ] The discriminator passes: naive all-in-SQL is wrong, the seam is right (Task 3).
- [ ] The seam matches §12.5 intersection-through-arrow-with-wildcard (Task 4).
- [ ] `SEAM_DECISION.md` records the seam: CTE = reachability, C# = algebra, Postgres → Core reference (Task 5).
