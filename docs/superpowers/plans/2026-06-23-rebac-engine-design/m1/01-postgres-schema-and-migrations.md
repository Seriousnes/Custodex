# M1/01 — Postgres Schema & Migrations

**Goal:** Stand up the `Custodex.Storage.Postgres` project (Dapper + Npgsql) and the full Postgres schema (spec §6.3) — `stores`, `schema_versions`, `tenants`, `relation_tuples`, `object_attributes`, `reverse_index` (stamped with `schema_version`), `cache_entries` (`UNLOGGED`), `change_log`, plus two infrastructure tables (`tenant_epochs` for the cache epoch counter, `schema_migrations` for the migration tracker) — applied by an idempotent embedded-SQL migration runner and verified against a real Postgres via Testcontainers.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; one task at a time, TDD (Red → Green → Commit), checkboxes track progress, one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach:** Migrations are plain `.sql` scripts embedded as assembly resources and applied in lexical order by a small `MigrationRunner`. The runner records each applied script in a `schema_migrations` tracker and skips already-applied scripts, so re-running is a no-op; it creates the tracker table itself, so it bootstraps on an empty database. No EF Core, no migration framework. Every tenant-scoped table carries `store_id` and `tenant_id` and FKs to `stores`/`tenants` so every storage operation can hard-filter on both (spec §6.3). `relation_tuples` carries a forward index (Check) and a reverse index (traversal / ListSubjects), plus a natural-key unique index whose `COALESCE(subject_relation,'')` collapses null vs empty subject-relation so the M1/04 upsert key never double-counts.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints (net10.0; warnings-as-errors; async + trailing `CancellationToken`; non-empty ordinal identifiers; `"*"` wildcard; Apache-2.0; **no EF Core**).

**Dependencies:** builds on `m0/01` (the `Custodex.Abstractions` contract — see README). The `tenant_epochs` and `schema_migrations` tables are engine infrastructure, not spec §6.3 tables; `cache_entries.epoch` is a per-row stamp, distinct from the `tenant_epochs` counter (README post-dispatch reconciliation 3).

---

### Task 1: Scaffold the project and test project

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj` (references Abstractions; Npgsql + Dapper; `Migrations/*.sql` as embedded resources) and `tests/Custodex.Storage.Postgres.Tests/Custodex.Storage.Postgres.Tests.csproj` (Shouldly + `Testcontainers.PostgreSql` + Npgsql + Dapper); test `…Tests/WiringTests.cs`, and later `…Tests/ResourceNameTests.cs`.

**Produces:** the `Custodex.Storage.Postgres` assembly and its test project, wired into `Custodex.slnx`.
**Consumes (see README):** `Custodex.Abstractions`.

**Behavior:** standard project scaffolding. The SQL scripts must be marked `<EmbeddedResource Include="Migrations/*.sql" />` — the runner loads them by manifest-resource name.

**Cases to pin:**

| Setup | Expect |
|---|---|
| reference the assembly | assembly name is `Custodex.Storage.Postgres` |
| inspect embedded resources | `001_initial_schema.sql` is embedded under the expected resource name |

**Done when:** build clean under TreatWarningsAsErrors; wiring + resource-name cases pass (no Postgres needed).

---

### Task 2: The initial schema migration script

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Migrations/001_initial_schema.sql`.

**Produces:** every table and index from spec §6.3, plus `tenant_epochs` and `schema_migrations` (the latter is created by the runner, not this script). Single source of DDL; Task 3 applies it.

**Behavior** (spec §6.1–§6.5, §9.1): one script, every statement `IF NOT EXISTS` so a partial run is safely re-runnable.
- `stores` (PK `id`); `schema_versions(store_id, version, definition jsonb, is_active)` with a partial unique index enforcing at most one active schema per store; `tenants(store_id, tenant_id)`.
- `relation_tuples` with nullable `subject_relation`, `condition_name`, and `condition_params jsonb`; three indexes — natural-key unique over `(store, tenant, object, relation, subject, COALESCE(subject_relation,''))`, forward `(store, tenant, object_type, object_id, relation)`, reverse `(store, tenant, subject_type, subject_id)`.
- `object_attributes` (`attributes jsonb`, PK on the object); `reverse_index` carrying `schema_version` + `conditioned`, with a scan index for ListObjects; `cache_entries` as an **`UNLOGGED`** table with `value bytea`, `epoch`, `expires_at` and an expiry index; `change_log` (`bigserial id`, `before`/`after` jsonb, `occurred_at default now()`) with a newest-first read index; `tenant_epochs(store, tenant, epoch bigint)`.
- All tenant-scoped tables FK to `tenants(store_id, tenant_id)`.

**Cases to pin:** asserted by Task 3's tests against a live container — see there.

**Done when:** script applies cleanly; covered by Task 3.

---

### Task 3: The idempotent migration runner

- [ ] **Files:** modify `src/Custodex.Storage.Postgres/MigrationRunner.cs`; tests `…Tests/PostgresFixture.cs` (shared Testcontainers fixture + `[CollectionDefinition("postgres")]`) and `…Tests/MigrationRunnerTests.cs`.

**Produces:** `MigrationRunner.ApplyAsync(NpgsqlConnection, CancellationToken)` — creates `schema_migrations` if absent, loads embedded `Migrations/*.sql` in ordinal name order, and for each not-yet-recorded version runs the script and its tracking insert in one transaction. Re-running applies nothing.
**Consumes (see README):** none beyond the assembly's own embedded resources.

**Behavior:** read applied versions from `schema_migrations`, skip them, apply the rest atomically (script + tracker row per transaction). The version string is the resource basename minus `.sql`. `PostgresFixture` starts one `postgres:16-alpine` container per collection and vends opened connections; every integration test in this project shares the `"postgres"` collection.

**Cases to pin:**

| Setup | Expect |
|---|---|
| apply on empty DB | every spec §6.3 table exists, plus `tenant_epochs`, `schema_migrations` |
| apply on empty DB | `ix_relation_tuples_forward`, `…_reverse`, the natural-key unique index, and the reverse-index scan index all exist |
| inspect `cache_entries` | `pg_class.relpersistence = 'u'` (UNLOGGED) |
| apply twice | `schema_migrations` records `001_initial_schema` exactly once (second run is a no-op) |

**Done when:** build clean; all four cases pass (Docker/Testcontainers required).

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors.
- [ ] Every spec §6.3 table exists; `cache_entries` is `UNLOGGED`.
- [ ] `relation_tuples` has the forward, reverse, and natural-key unique indexes.
- [ ] `tenant_epochs` and `schema_migrations` exist (engine infrastructure, not §6.3).
- [ ] `ApplyAsync` is idempotent: a second run inserts no new tracker rows.
