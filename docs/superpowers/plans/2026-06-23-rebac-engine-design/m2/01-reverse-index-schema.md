# M2/01 — Reverse-Index Schema & `IIndexStore`

**Goal:** Populate the `IIndexStore` contract (empty in the foundation README today) and implement `NpgsqlIndexStore` over the `reverse_index` table from m1/01, plus the `ReverseIndexRow` row value. This is the storage seam the full rebuild (m2/02) and incremental maintenance (m2/03) write through, and the index-backed `ListObjects` (m2/04) reads through.

**For implementers:** drive this with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each `### Task` is one TDD unit — write the failing test (Red), make it pass (Green), then one Conventional-Commit per task with the co-author trailer (see `../README.md` → Global Constraints). Tasks are tracked with `- [ ]` checkboxes.

**Architecture/approach:** The `reverse_index` table holds resolved **structural (unconditioned) grants**: `(store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)`. A row asserts "`subject` holds `permission` on `object_type:object_id` structurally; when `conditioned` is true, a request-time condition was reached on the grant path and must be re-evaluated before the grant is honoured." `subject` is the canonical `SubjectRef` string (`user:alice`, `group:vets#member`, `user:*`). The store is a thin, mechanical CRUD seam — no algebra lives here; the expansion that decides which rows exist lives in m2/02/m2/03. Every operation hard-filters `(store_id, tenant_id, schema_version)` (spec §6.3, §7.3).

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, Testcontainers.PostgreSql.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/01 (`Custodex.Abstractions`, where `IIndexStore` lives — empty today); m1/01 (`reverse_index` DDL, `MigrationRunner`, `PostgresFixture`); m1/03 (`NpgsqlUnitOfWork`/`NpgsqlUnitOfWorkFactory` — index writes enlist in the caller's unit of work so they commit with the data write); m1/04 (the `NpgsqlRelationStore`/`NpgsqlSchemaStore` patterns).

## Shared decisions (used by m2/02, m2/03, m2/04, m2/06)

These M2 plans share the index table and its row shape:

- **Natural key** (the identity an upsert/delete targets) is `(store_id, tenant_id, schema_version, subject, permission, object_type, object_id)`. `conditioned` is **not** in the key — it is a mutable attribute, so an unconditioned grant becoming conditioned (or vice versa) is an in-place update, not a new row.
- **`subject` encoding** is the canonical `SubjectRef.ToString()`: `type:id` for a concrete or wildcard subject, `type:id#relation` for a subject-set. The index stores rows only for the **query subjects** `ListObjects` is asked about — concrete users (`user:alice`) and the public wildcard (`user:*`). Intermediate subject-sets are expanded away during rebuild/maintenance and never stored.
- **Unique index** enforces the natural key so `ON CONFLICT` upserts are well-defined (added by this plan's migration 002).
- **Schema-version stamping**: every row carries the `schema_version` active when it was computed. m1/01's `ix_reverse_index_scan` already leads with `(store, tenant, schema_version, subject, permission, object_type)` so the scan and the staleness check share one index.

---

### Task 1: `reverse_index` natural-key unique index (migration 002)

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Migrations/002_reverse_index_unique.sql`; test `…Tests/Index/ReverseIndexSchemaTests.cs`.

**Produces:** a unique index `ux_reverse_index_natural` on the locked natural key, giving Task 4's upsert a conflict target. Applied by the existing `MigrationRunner` (m1/01), which picks up embedded `Migrations/*.sql` in name order.
**Consumes (see README):** `MigrationRunner`, `PostgresFixture` (m1/01).

**Behavior:** m1/01's `001_initial_schema.sql` created `reverse_index` and the *scan* index `ix_reverse_index_scan` but no unique constraint on the natural key (M2 owns the maintenance/upsert semantics, so the conflict target lands here). The migration adds `ux_reverse_index_natural` over the seven natural-key columns; `conditioned` is excluded so flipping it is an in-place UPDATE, not a duplicate row. Idempotent (`CREATE UNIQUE INDEX IF NOT EXISTS`) like every other script.

**Cases to pin:**

| Setup | Expect |
|---|---|
| apply migrations, list `reverse_index` indexes | contains `ux_reverse_index_natural` and `ix_reverse_index_scan` (m1/01 still present) |
| insert a row, then insert same natural key with a different `conditioned` flag | second insert raises `PostgresException` SQLSTATE `23505` (unique_violation) — conditioned is not in the key |

**Done when:** build clean; cases pass; requires Docker (Testcontainers Postgres).

---

### Task 2: `IIndexStore` members + `ReverseIndexRow`

- [ ] **Files:** modify `src/Custodex.Abstractions/Storage.cs`; create `src/Custodex.Abstractions/ReverseIndexRow.cs`; test `tests/Custodex.Abstractions.Tests/IndexStoreContractTests.cs`.

**Produces:** the populated `IIndexStore` interface (README ships it empty) and the `ReverseIndexRow` record. This plan is the **definer** of the full `IIndexStore` member set; the README "Post-dispatch contract reconciliations" §4 is its canonical home, and §4's enumerated core (`UpsertAsync` / `DeleteForObjectAsync` / a query scan / rebuild markers) is the contract this realizes. The members, all `(store, tenant, schema_version)`-scoped and `async` with a trailing `ct`:
  - upsert rows in-place on the natural key, setting `conditioned`;
  - delete every row for one object (the unit incremental maintenance recomputes, m2/03);
  - delete specific rows by natural key (targeted incremental diffs, m2/03);
  - query objects for one `(subject, permission, objectType)`, ordinal-sorted by `object_id`, strictly after a cursor id, capped at a limit — the `ListObjects` scan;
  - read every row for one object (the "current rows" side of an incremental diff, m2/03);
  - clear **all** index rows for a `(store, tenant)` across every schema_version (start of a full rebuild, schema-change invalidation, m2/02);
  - has-the-index-been-built marker read and write per `(store, tenant, schema_version)` — the staleness check (spec §7.3): a schema change yields a new version whose marker is absent, so a stale index is never served.

  `ReverseIndexRow` carries only the per-row columns (`Subject`, `Permission`, `ObjectType`, `ObjectId`, `Conditioned`); the scope travels alongside so a row value is reusable across schema versions in tests.
**Consumes (see README):** `TenantContext`, `IUnitOfWork` (`Custodex.Abstractions`).

**Behavior:** the query-scan, delete-rows, read-for-object, and clear members exist beyond §4's core because the rebuild (m2/02) and the exclusion-correct incremental diff (m2/03) need them; report the full set so the README §4 home can carry the realized signatures. Each `///` doc comment on the public surface states the contract and the `(store, tenant, schema_version)` scoping.

**Cases to pin:**

| Setup | Expect |
|---|---|
| construct a `ReverseIndexRow("user:alice","edit","species","kangaroo", Conditioned:false)` | each component round-trips; `Conditioned` is false |
| reflect over `typeof(IIndexStore)` | declares the upsert, delete-for-object, delete-rows, query-objects, read-for-object, clear, is-built, and mark-built members |

**Done when:** build clean; cases pass; `IIndexStore` is no longer empty; the new member set is reported as a contract addition for README §4.

---

### Task 3: `index_build_markers` table (migration 003)

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Migrations/003_index_build_markers.sql`; test `…Tests/Index/IndexBuildMarkerSchemaTests.cs`.

**Produces:** `index_build_markers(store_id, tenant_id, schema_version, built_at, PK(store_id, tenant_id, schema_version))`, the table backing the is-built / mark-built members.
**Consumes (see README):** `MigrationRunner`, `PostgresFixture`; FK to `tenants(store_id, tenant_id)`.

**Behavior:** a marker row's presence means "the index is current for this `(store, tenant, schema_version)`." This realizes the spec §7.3 staleness check: a schema change produces a new `schema_version` with no marker, so index-backed `ListObjects` (m2/04) sees the index is not built for the active version and falls back / triggers a rebuild rather than serving stale rows. `built_at` defaults to `now()`. The table is M2 maintenance infrastructure, not a spec §6.3 table — sanctioned in the same way as m1/01's `tenant_epochs` / `schema_migrations`; reported, not a README change.

**Cases to pin:**

| Setup | Expect |
|---|---|
| apply migrations, query `information_schema.tables` | `index_build_markers` exists |

**Done when:** build clean; case passes; requires Docker.

---

### Task 4: `NpgsqlIndexStore`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlIndexStore.cs`; test `…Tests/Index/NpgsqlIndexStoreTests.cs`.

**Produces:** `NpgsqlIndexStore(string connectionString) : IIndexStore` — every member in Dapper over `reverse_index` and `index_build_markers`.
**Consumes (see README):** `IIndexStore` / `ReverseIndexRow` / `TenantContext` (Task 2); `NpgsqlUnitOfWork.From` (m1/03); Dapper.

**Behavior:** mechanical CRUD, no algebra — the store stores, deletes, and reads exactly what m2/02/m2/03 hand it. Writes enlist in the supplied unit of work (so they commit with the data write); reads open their own short-lived connection. Every statement hard-filters `(store_id, tenant_id)` and, where row-scoped, `schema_version`. Upsert is insert-or-update on the natural key, updating `conditioned` from the excluded row. The query scan rides m1/01's `ix_reverse_index_scan` and orders by `object_id` ordinal for stable pagination, returning rows strictly after the cursor id capped at the limit. Clear deletes both the `reverse_index` rows and the `index_build_markers` rows for the `(store, tenant)`. Mark-built upserts the marker row, refreshing `built_at`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| upsert three rows (two for `user:alice`, one for `user:bob`), query `user:alice` | alice's two object ids, sorted; bob's excluded |
| query with `limit:2`, cursor `"a"` over ids a/b/c | `[b,c]` — strictly after cursor, cap not hit |
| upsert same natural key twice flipping `conditioned` | single row, `conditioned` now true (in-place, not duplicated) |
| delete-for-object on `species:kangaroo` with rows on kangaroo + wallaby | kangaroo rows gone; wallaby untouched |
| delete-rows for one specific `(subject, perm, type, id)` of two on an object | only the named row removed |
| mark-built `v1`, then is-built `v1` and `v2` | `v1` true, `v2` false (per-version) |
| upsert rows under `v1` and `v2`, then clear | both versions empty |

**Done when:** build clean; cases pass; upsert is in-place on the natural key, the scan is ordinal-sorted with a cursor, and every statement hard-filters tenant (and `schema_version` where row-scoped); requires Docker.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [ ] `IIndexStore` is no longer empty: it declares the upsert, delete-for-object, delete-rows, query-objects, read-for-object, clear, is-built, and mark-built members (reported as a contract addition for README §4).
- [ ] `ReverseIndexRow(Subject, Permission, ObjectType, ObjectId, Conditioned)` exists in `Custodex.Abstractions`.
- [ ] `reverse_index` has `ux_reverse_index_natural` (natural key, `conditioned` excluded) and keeps `ix_reverse_index_scan`.
- [ ] `index_build_markers` exists; presence marks the index current for a `(store, tenant, schema_version)`.
- [ ] `NpgsqlIndexStore` upserts in-place, scans ordinal-sorted with a cursor, and hard-filters `(store, tenant[, schema_version])` on every statement.

## Contract gaps / additions (reported, not changed)

- **`IIndexStore` member set + `ReverseIndexRow` (contract addition).** README ships `IIndexStore` empty; this plan defines the full member set. README §4 is its canonical home; report the realized signatures for the maintainer to fold in. No README edit by this plan.
- **`index_build_markers` (non-§6.3 table).** Maintenance infrastructure for the spec §7.3 staleness check, mirroring m1/01's `tenant_epochs`/`schema_migrations`.
- **`ux_reverse_index_natural` unique index.** m1/01 created the table and scan index but no unique constraint; M2 owns the upsert semantics, so it is added here.
