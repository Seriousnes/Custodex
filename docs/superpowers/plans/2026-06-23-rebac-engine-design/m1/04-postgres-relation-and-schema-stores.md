# M1/04 — Postgres Relation, Schema, Attribute & ChangeLog Stores

**Goal:** Dapper implementations of `IRelationStore`, `ISchemaStore`, `IAttributeStore`, and `IChangeLogStore` against the M1/01 schema, **every statement hard-filtering on `store_id + tenant_id`** (spec §6.3). Map `ConditionRef` parameters and attribute dictionaries to/from `jsonb`, and round-trip the polymorphic `Schema` AST. Prove tenant isolation with a cross-tenant test.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach:** Each store is a thin Dapper class. Reads open their own short-lived `NpgsqlConnection` from the configured connection string; writes run through the live `NpgsqlConnection`/`NpgsqlTransaction` carried by the `IUnitOfWork` (resolved via `NpgsqlUnitOfWork.From`, M1/03) so they commit inside the caller's transaction. A shared `Json` helper centralizes jsonb (de)serialization; a `PermExprJsonConverter` `JsonConverterFactory` round-trips the abstract `PermExpr`/`ConditionExpr` AST via a `$type` discriminator (the contract records carry no `[JsonPolymorphic]` attributes, so the provider supplies the converter); a `DapperConfig` module initializer enables underscore name matching so snake_case columns fill PascalCase record fields. `WriteAsync` upserts adds via `ON CONFLICT` on the natural key (M1/01) and deletes removes by the same key. `store_id + tenant_id` is a non-optional predicate in every query.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints. **No EF Core.**

**Dependencies:** builds on `m0/01` (contracts — `IRelationStore`/`ISchemaStore`/`IAttributeStore`/`IChangeLogStore`, `RelationTuple`, `ConditionRef`, `Schema`/`PermExpr`, `TupleFilter`, `ChangeLogFilter`/`ChangeLogEntry`; see README), M1/01 (schema, `MigrationRunner`, `PostgresFixture`), M1/03 (`NpgsqlUnitOfWork`/factory).

---

### Task 1: The `Json` helper, polymorphic `PermExpr` converter, and Dapper config

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Json.cs`, `…/PermExprJsonConverter.cs`, `…/DapperConfig.cs`; test `…Tests/JsonTests.cs`.

**Produces:** `Json.Serialize`/`Deserialize<T>` over `System.Text.Json` (web defaults) with the converter registered; `PermExprJsonConverter` (a `JsonConverterFactory` covering both `PermExpr` and `ConditionExpr`); `DapperConfig` enabling `DefaultTypeMap.MatchNamesWithUnderscores`.
**Consumes (see README):** `PermExpr` node set (`RelationRef`/`Union`/`Intersect`/`Exclude`/`Arrow`/`Conditioned`), `ConditionExpr`, `Schema`.

**Behavior:** the converter writes a `$type` discriminator per concrete node and re-reads it (buffering through a `JsonObject` for order-independence), stripping itself from the options clone to avoid infinite recursion. Both are load-bearing: without underscore matching every snake_case column silently maps to `null`/default; without polymorphism `System.Text.Json` cannot round-trip the abstract AST that `NpgsqlSchemaStore` persists to jsonb.

**Cases to pin:**

| Setup | Expect |
|---|---|
| dictionary round-trip | values preserved |
| `Deserialize<T>(null)` | default/null |
| nested `Exclude(Union(RelationRef, Arrow), RelationRef)` round-trip | structure and node types preserved |
| full `Schema` round-trip through jsonb text | version + permission expression node type preserved |

**Done when:** build clean; all four cases pass (no Postgres).

---

### Task 2: `NpgsqlRelationStore`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlRelationStore.cs`; test `…Tests/RelationStoreTests.cs`.

**Produces:** `NpgsqlRelationStore(connectionString) : IRelationStore` — `GetByObjectAsync`, `GetBySubjectAsync`, `ListObjectIdsAsync`, and `WriteAsync(add, remove, uow)`. Maps `condition_name`/`condition_params` ⇄ `ConditionRef`.
**Consumes (see README):** `IRelationStore`, `RelationTuple`, `EntityRef`/`SubjectRef`, `ConditionRef`, `IUnitOfWork`; `Json` (Task 1).

**Behavior:** reads select the tuple columns plus `condition_params::text` and filter `store + tenant` (+ object/relation, or subject identity with `COALESCE(subject_relation,'')` matching). `WriteAsync` deletes removes by the natural key, then upserts adds `ON CONFLICT` on the natural key, **updating `condition_name`/`condition_params`** on conflict. `ListObjectIdsAsync` returns the distinct object ids of a type within the tenant (the type-universe primitive the ListObjects oracle and wildcard grants depend on). Subject-relation null vs `''` never collides because the key coalesces it.

**Cases to pin:**

| Setup | Expect |
|---|---|
| write then get-by-object a conditioned subject-set tuple | round-trips subject and condition (name + params) |
| write then remove a tuple | get-by-object empty (deleted by natural key) |
| get-by-subject for a granted user | returns the tuple, correct object |
| upsert the same natural key with a new condition | the stored condition is updated |
| `ListObjectIdsAsync(type)` across two tenants | distinct ids for the type, tenant-isolated |

**Done when:** build clean; all five cases pass (Postgres required).

---

### Task 3: Cross-tenant isolation

- [ ] **Files:** test `…Tests/CrossTenantIsolationTests.cs` (no production code).

**Consumes (see README):** `NpgsqlRelationStore`.

**Behavior:** writing into tenant A and reading from tenant B (same store) returns nothing — by object and by subject — while tenant A still sees its own tuple. Proves the `store_id + tenant_id` hard filter. If it fails, a query is missing the tenant predicate; fix the store.

**Cases to pin:**

| Setup | Expect |
|---|---|
| write in tenant A, read in tenant B | empty by object and by subject; tenant A still sees it |

**Done when:** build clean; the case passes (Postgres required).

---

### Task 4: `NpgsqlAttributeStore`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlAttributeStore.cs`; test `…Tests/AttributeStoreTests.cs`.

**Produces:** `NpgsqlAttributeStore(connectionString) : IAttributeStore` — `GetAsync` (null when absent) and `SetAsync(uow)` (upsert on the object PK), mapping the attribute dictionary ⇄ the `attributes` jsonb column, hard-filtering `store + tenant`.
**Consumes (see README):** `IAttributeStore`, `EntityRef`; `Json` (Task 1).

**Cases to pin:**

| Setup | Expect |
|---|---|
| set then get | attributes round-trip |
| set twice | the later value replaces the earlier (upsert) |
| get an absent object | null |

**Done when:** build clean; all three cases pass (Postgres required).

---

### Task 5: `NpgsqlSchemaStore`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlSchemaStore.cs`; test `…Tests/SchemaStoreTests.cs`.

**Produces:** `NpgsqlSchemaStore(connectionString) : ISchemaStore` — `GetActiveAsync(store)` and `SetActiveAsync(store, schema, uow)`. The AST serializes to the `definition` jsonb column (via the Task-1 converter); setting active deactivates the previously active version for the store, then upserts the new one as active. Schema is per-store (not tenant-scoped), matching the contract.
**Consumes (see README):** `ISchemaStore`, `Schema`; `Json` (Task 1).

**Cases to pin:**

| Setup | Expect |
|---|---|
| set active v1, get active | round-trips version + type names |
| set active v1 then v2 | active reads v2 (v1 superseded) |
| get active for an unknown store | null |

**Done when:** build clean; all three cases pass (Postgres required).

---

### Task 6: `NpgsqlChangeLogStore`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlChangeLogStore.cs`; test `…Tests/ChangeLogStoreTests.cs`.

**Produces:** `NpgsqlChangeLogStore(connectionString) : IChangeLogStore` — `AppendAsync(uow)` (the DB generates `id` via `bigserial` and `occurred_at` via `default now()`, so the entry's passed `Id`/`OccurredAt` are ignored) and `ReadAsync(filter)` (newest-first, filtered by `Since`/`Actor`, capped at `Limit`). Maps `before`/`after` ⇄ jsonb.
**Consumes (see README):** `IChangeLogStore`, `ChangeLogEntry`, `ChangeLogFilter`; `Json` (Task 1).

**Cases to pin:**

| Setup | Expect |
|---|---|
| append then read | entry has a DB-generated id (> 0) and a DB `occurred_at` (not the passed Unix-epoch value); actor/operation preserved |
| append three (two by one actor), read filtered + capped | actor filter returns that actor's two; `Limit:1` returns the newest one |

**Done when:** build clean; both cases pass (Postgres required).

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors.
- [ ] Every read and write filters `store_id` + `tenant_id` (schema store filters `store_id`; schema is per-store).
- [ ] Cross-tenant isolation holds (tenant A invisible from tenant B by object and by subject).
- [ ] Condition params, attributes, and the full `Schema` AST round-trip through jsonb via the shared `Json` helper + converter.
- [ ] `WriteAsync` upserts adds on the natural key (updating the condition) and deletes by the same key; writes flow through the `IUnitOfWork`.
- [ ] Change-log `id`/`occurred_at` are DB-generated; `ReadAsync` is newest-first, filtered, capped.
- [ ] `DapperConfig` underscore matching is enabled, else every snake_case column maps to null.
