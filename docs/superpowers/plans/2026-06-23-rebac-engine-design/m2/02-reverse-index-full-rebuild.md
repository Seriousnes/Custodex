# M2/02 — Reverse-Index Full Rebuild

**Goal:** Implement `ReverseIndexRebuilder` — a full rebuild of `reverse_index` for a `(store, tenant)` from the current tuples and the active schema, materializing every `(subject, permission, object)` **structural** grant, stamping each row with the active `schema_version` and a `conditioned` flag. This rebuild is the **always-correct safety net** and the **maintenance oracle** the incremental path (m2/03) is diffed against (m2/06).

**For implementers:** drive this with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each `### Task` is one TDD unit — Red → Green → one Conventional-Commit with the co-author trailer (see `../README.md` → Global Constraints). Tasks are tracked with `- [ ]` checkboxes.

**Architecture/approach:** The rebuild computes the same structural truth the m0/07 `EngineDrivenAuthorizer` ListObjects oracle computes, then **stores it** as `reverse_index` rows. For each `(objectType, permission)` in the schema, each candidate object of that type, and each candidate subject (the concrete users plus the public `user:*`), it asks the **unconditioned** pointwise Check whether the grant holds; when it does, it writes a row whose `conditioned` flag records whether any condition was reached on the grant path. "Unconditioned" is achieved by evaluating with `NullConditionEvaluator` (every condition treated as satisfied), so the stored row reflects the *structural* grant; the `ConditionTouched` latch on `EvalContext` (m0/05) decides the flag. ListObjects (m2/04) re-checks only flagged rows at query time, keeping request-time predicates correct (spec §7.3).

The rebuild runs through the **oracle authorizer** (`EngineDrivenAuthorizer`), not the CTE path: its job is to be obviously, durably correct (it is the safety net), and the oracle is the project's ground truth (spec §7.4). It is heavier than incremental maintenance, which is the point — correctness over speed.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, Testcontainers.PostgreSql. Reuses `Custodex.Core` (`EngineDrivenAuthorizer`, `SchemaIndex`, `EvalContext`, `NullConditionEvaluator`).

**Global Constraints:** see `../README.md` → Global Constraints, and its Calibration note.

**Dependencies:** builds on m0/05 (`EngineDrivenAuthorizer`, `EvalContext.ConditionTouched`); m0/07 (the ListObjects oracle semantics this materializes, the type-universe shape); m1/01–m1/04 (`reverse_index`, `MigrationRunner`, `PostgresFixture`, `NpgsqlUnitOfWork*`, the Npgsql relation/schema/attribute stores); m2/01 (`IIndexStore`/`NpgsqlIndexStore`, `ReverseIndexRow`).

> **Calibration.** The rebuild is the *safety net*, written to be correct by construction: it materializes the oracle's already-proven structural truth. The **tests are the spec** — every rebuild test asserts the stored rows equal what the oracle's `ListObjects`/`Check` says structurally. m2/03's incremental maintenance is correct iff it produces the same rows this rebuild does, proven by the m2/06 differential harness. Keep this path simple and obviously-correct even where slow.

---

### Task 1: Structural-grant probe on the oracle

- [ ] **Files:** create `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Structural.cs`; test `tests/Custodex.Core.Tests/Evaluation/StructuralProbeTests.cs`.

**Produces:** a **public** structural-grant probe on `EngineDrivenAuthorizer` returning a new public `StructuralGrant(bool Granted, bool Conditioned)`. Public because the cross-assembly rebuilder (`Custodex.Storage.Postgres`) calls it — the same precedent as `SchemaIndex`/`EvalContext` being public in `Custodex.Core.Evaluation`. The probe takes a `SchemaIndex`, `TenantContext`, `EntityRef`, permission, `SubjectRef`, `RequestContext` (mirroring the private Check walk's parameters) plus the trailing `ct`. An `internal` test seam forwards to it (m0/07's `[InternalsVisibleTo("Custodex.Core.Tests")]` exposes internals to the test project).
**Consumes (see README):** the private Check walk, `EvalContext`, `SchemaIndex` (m0/05).

**Behavior:** runs the pointwise Check under a fresh `EvalContext`, always with conditions treated as satisfied (the authorizer is constructed with `NullConditionEvaluator` for rebuild). Reports `Granted`, plus `Conditioned` = whether `EvalContext.ConditionTouched` latched. KEY DECISIONS:
- Constructing the authorizer with `NullConditionEvaluator` makes every condition pass, so a grant that exists *only when a condition holds* is recorded as a **structural** grant flagged `conditioned = true`; a grant with no condition on its path is `conditioned = false` and honoured directly.
- `Conditioned` is reported **only on a granted path** — a denied check may short-circuit before reaching a condition, and no rows are stored for denials, so the latch is meaningless unless `granted`.

**Cases to pin** (each via the in-memory store + `NullConditionEvaluator`):

| Setup | Expect |
|---|---|
| `viewer@alice` direct, `view = viewer` | `Granted` true, `Conditioned` false |
| `viewer@alice` carrying a `within_hours` condition | `Granted` true (condition treated satisfied), `Conditioned` true (flag for query-time re-check) |
| no grant for the subject | `Granted` false |

**Done when:** build clean; cases pass; the probe is public; reported as a new `Custodex.Core` surface (not an Abstractions change).

---

### Task 2: Enumerate the rebuild domain — subjects, objects

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Index/RebuildEnumeration.cs`; test `…Tests/Index/RebuildEnumerationTests.cs`.

**Produces:** `RebuildEnumeration` static helpers that read the tenant's tuples once and project the rebuild domain: the concrete `user` ids that appear as a subject anywhere (the candidate query subjects), and every object id grouped by object type (the candidate objects). `user:*` is not in the user list — the rebuilder adds the synthetic `*` subject. The rebuilder crosses these with the schema's `(type, permission)` declarations.
**Consumes (see README):** `relation_tuples` (m1/01); `TenantContext`.

**Behavior:** a reverse-index row is `(subject, permission, object_type, object_id)`. The candidate **subjects** are the concrete users in the tenant plus the public `user:*` (other subject types are never the query subject of a `ListObjects`). The candidate **objects** are every object of each type that appears in any tuple — the type universe, exactly as the m0/07 oracle / m1/06 CTE compute it. Probing each `(user, permission, object)` and keeping the granted ones is the rebuild: O(users × objects × permissions) Checks, acceptable at the spec's target scale (dozens-to-hundreds of users, low-thousands of resources) and acceptable because this is the safety net, not the hot path. The user query filters `subject_type = 'user'` and excludes `'*'`; the object query groups distinct `(object_type, object_id)` ordinally.

**Cases to pin:**

| Setup | Expect |
|---|---|
| tuples naming `user:alice`, `user:bob`, `user:*`, plus species/group objects | users = `[alice, bob]` (`*` excluded); objects-by-type has species ids and group ids |

**Done when:** build clean; case passes; requires Docker.

---

### Task 3: `ReverseIndexRebuilder` — probe, materialize, stamp, mark built

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Index/ReverseIndexRebuilder.cs`; test `…Tests/Index/ReverseIndexRebuildTests.cs`.

**Produces:** `ReverseIndexRebuilder(connectionString, ISchemaStore, IRelationStore, IAttributeStore, IIndexStore)` with `RebuildAsync(TenantContext, IUnitOfWork, ct)`.
**Consumes (see README):** `RebuildEnumeration` (Task 2); the structural probe (Task 1); `IIndexStore` (m2/01); `SchemaIndex`, `NullConditionEvaluator`, `EngineDrivenAuthorizer` (m0/05); the Npgsql stores (m1/04).

**Behavior:** the maintenance oracle — deliberately the simple, exhaustive, obviously-correct construction. `RebuildAsync`:
1. loads the active `Schema` (its `Version` is the stamp); throws `UnknownTypeException` if none is active;
2. clears all prior rows + markers for the tenant (idempotent rebuild);
3. loads the rebuild domain (Task 2);
4. constructs an `EngineDrivenAuthorizer` over the **same stores** with `NullConditionEvaluator`;
5. for each schema `(type, permission)`, each candidate object id of that type, and each candidate subject (`user:{id}` ∪ `user:*`), probes the structural grant; for each granted probe writes a `ReverseIndexRow(subject, permission, type, objectId, conditioned)` stamped with `schema.Version`;
6. marks the index built for `schema.Version`.

All writes go on the caller's unit of work so the rebuild commits atomically (spec §9.2). KEY DECISIONS:
- **Probe per-grant, not per-subject ListObjects.** Reusing the per-`(subject, object, permission)` probe keeps the `conditioned` flag exact (the latch is per-check) and the rebuild trivially correct. Materializing via `ListObjects` per subject would give the same granted ids but lose the per-row `conditioned` bit.
- A multi-path object yields exactly one row per natural key (paths collapse); exclusion (a `blocked` grant) means no grant, hence no row.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `editor@group:macropods#member` on kangaroo+wallaby; alice in macropods; `blocked@alice` on wallaby | rebuild marks v1 built; `user:alice` edits `[kangaroo]` (wallaby excluded); rows `conditioned=false` |
| `editor@user:*` on emu | `user:*` edits `[emu]` (wildcard stored under the star subject) |
| `editor@dr-smith` on kangaroo carrying `within_hours` | one row for `user:dr-smith` on kangaroo, `conditioned=true` |
| rebuild twice | single row per natural key — idempotent, no duplicates |

**Done when:** build clean; cases pass; rows are structural with `conditioned` from the latch; every row stamped with `schema.Version` and the version marked built; writes on the caller's uow; requires Docker.

---

### Task 4: Rebuild ≡ oracle's ListObjects (safety-net anchor)

- [ ] **Files:** test only `…Tests/Index/RebuildEqualsOracleTests.cs` — no new production code.

**Produces:** the concrete-case anchor for the safety-net invariant the m2/06 property harness generalizes.
**Consumes (see README):** `ReverseIndexRebuilder`, `NpgsqlIndexStore`, `EngineDrivenAuthorizer`, the Npgsql stores.

**Behavior — the invariant being asserted:** for a §12.5-style structural gate with a multi-path object, the rebuilt index's unconditioned rows, read per subject, equal `EngineDrivenAuthorizer.ListObjectsAsync` for that subject. This proves the rebuild materializes the oracle, so m2/03 has a trustworthy maintenance oracle to diff against.

**Cases to pin:**

| Setup | Expect |
|---|---|
| kangaroo editable by alice via **both** a direct grant and group membership; wallaby via group; `blocked@alice` on wallaby; alice+bob in macropods | per-subject index rows == oracle `ListObjects`: alice `[kangaroo]` (multi-path collapses to one row, wallaby blocked), bob `[kangaroo, wallaby]` |

**Done when:** build clean; the per-subject index-vs-oracle equality holds; requires Docker.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [ ] Rebuild runs through `EngineDrivenAuthorizer` with `NullConditionEvaluator`, so rows are structural and `conditioned` comes from the `ConditionTouched` latch.
- [ ] Every row stamped with the active `schema.Version`; the version is marked built (spec §7.3 stamp).
- [ ] A multi-path object yields one row per `(subject, permission, type, objectId)` (Task 4).
- [ ] Exclusion is honoured: a `blocked` grant removes the row (Task 3).
- [ ] Idempotent: clear precedes re-materialization; a second rebuild produces the same rows.
- [ ] All index writes on the caller's `IUnitOfWork` — atomic commit (spec §9.2).
- [ ] Rebuilt index equals `EngineDrivenAuthorizer.ListObjects` per subject (Task 4) — the safety-net invariant m2/03 is diffed against.

## Contract gaps / additions (reported, not changed)

- **`EngineDrivenAuthorizer` structural probe + `StructuralGrant` (new public `Custodex.Core` surface).** Task 1 adds a public structural-grant probe so the cross-assembly rebuilder can call it, matching the precedent that `SchemaIndex`/`EvalContext` are public in `Custodex.Core.Evaluation`. A `Custodex.Core` addition, not an `Custodex.Abstractions` contract change; reported for visibility, no README edit.
