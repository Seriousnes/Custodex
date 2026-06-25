# M1/09 — DI & Usable Library

**Goal:** Make Custodex a usable library: concrete `IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager` implementations that supply the `actor` and before/after diffs and drive M1/07's `AuditedWritePath` so the data write, `change_log` entry, and cache-epoch bump all commit in **one** unit of work (spec §6.5 / §9.2); the `services.AddCustodex().UsePostgres(conn).UseSchema(builder)` DI surface (spec §10.1); `AddCustodexInstrumentation()` wiring the `"Custodex"` `ActivitySource` + `Meter` into OpenTelemetry (spec §11.4); and an end-to-end Testcontainers sample (define schema → write tuples → Check/ListObjects) proving the wired-up engine works against real Postgres.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach:** the DI base (`CustodexBuilder`, `AddCustodex()`, the `UseSchema` capture, and `AddCustodexInstrumentation()`) lives in **`Custodex.Core`** — there is no separate `Custodex.Extensions.DependencyInjection` project (README layout + post-dispatch reconciliation 7). `UsePostgres(...)` is an extension on `CustodexBuilder` shipped in `Custodex.Storage.Postgres` (so a consumer takes the Postgres dependency only when they call it; Core never references Postgres). The concrete managers live in `Custodex.Storage.Postgres` because they orchestrate `NpgsqlUnitOfWorkFactory` + `AuditedWritePath`: each write carries a caller-supplied `actor` (the engine never invents identity, spec §6.5), the manager assembles before/after images, opens one owned `IUnitOfWork`, delegates the in-transaction sequencing to `AuditedWritePath`, then commits — so the data write + `change_log` + epoch bump are atomic. `AddCustodexInstrumentation()` enables the library's `"Custodex"` source/meter (`CustodexDiagnostics`, M0/01) inside an existing OpenTelemetry pipeline.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, `Microsoft.Extensions.DependencyInjection.Abstractions`, `OpenTelemetry` (extension registration only), xUnit, Shouldly, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints. **No EF Core.** `AddCustodexInstrumentation()` registers into the existing ServiceDefaults OTel pipeline; it must not stand up its own (README → Aspire integration).

**Dependencies:** builds on `m0/01` (contracts + `CustodexDiagnostics`), `m0/02`/`m0/03` (`SchemaBuilder`, `SchemaValidator`), M1/01–M1/05/M1/07 (Postgres schema, UoW, stores, `NpgsqlCteAuthorizer`, `AuditedWritePath`, `PostgresCacheStore` — see README).

---

### Task 1: `AddCustodex()` builder entry point (in `Custodex.Core`)

- [ ] **Files:** create `src/Custodex.Core/CustodexBuilder.cs` and `…/CustodexServiceCollectionExtensions.cs`; test in `tests/Custodex.Core.Tests` (`CustodexBuilderTests`).

**Produces:** `IServiceCollection.AddCustodex() -> CustodexBuilder`; `CustodexBuilder` exposing `IServiceCollection Services { get; }`, `Schema? StartupSchema { get; }`, and `UseSchema(Schema)` / `UseSchema(SchemaBuilder)` capturing the startup schema for host-driven activation.
**Consumes (see README):** `Schema` (M0/01), `SchemaBuilder` (M0/02), `Microsoft.Extensions.DependencyInjection.Abstractions`.

**Behavior:** `AddCustodex()` returns a builder over the same `IServiceCollection`. `UseSchema` only *captures* the schema on `StartupSchema`; the registration does not validate or activate it — the consuming host reads `StartupSchema` and calls `ISchemaManager.SetActiveSchemaAsync` at the right point in startup.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `AddCustodex()` | returns a builder over the same services |
| `.UseSchema(builder)` | `StartupSchema` captured with the built version |

**Done when:** build clean; both cases pass (no Postgres).

---

### Task 2: The concrete managers — actor + diff orchestration over `AuditedWritePath`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Managers/CustodexRelationManager.cs`, `…/CustodexSchemaManager.cs`, `…/CustodexStoreTenantManager.cs` (holds `CustodexStoreManager` + `CustodexTenantManager`); add a filtered `QueryAsync(TupleFilter)` to `NpgsqlRelationStore`; test `…Tests/Managers/RelationManagerTests.cs`.

**Produces:**
- `CustodexRelationManager : IRelationManager` — `WriteTuplesAsync`/`DeleteTuplesAsync`/`WriteAttributesAsync` open an owned `IUnitOfWork`, compute before/after, call `AuditedWritePath`, and commit; `ReadTuplesAsync` (via the relation store's filtered `QueryAsync`) and `ReadChangeLogAsync` (via the change-log store).
- `CustodexSchemaManager : ISchemaManager` — `ValidateSchema` (delegates to M0/03 `SchemaValidator`), `SetActiveSchemaAsync` (validates then `AuditedWritePath.SetSchemaAsync` in one uow; throws `SchemaValidationException` on invalid), `GetActiveSchemaAsync`.
- `CustodexStoreManager : IStoreManager` and `CustodexTenantManager : ITenantManager` — insert the `stores` / `tenants` rows (`ON CONFLICT DO NOTHING`).

**Consumes (see README):** `AuditedWritePath`, `NpgsqlUnitOfWorkFactory`, the Npgsql stores (M1/03/04/07); `SchemaValidator` (M0/03); the manager interfaces + `SchemaValidationException` + `TupleFilter`/`ChangeLogFilter` (M0/01).

**Behavior** (spec §6.5 / §9.2): the managers own actor + diff assembly (the M1/07 hand-off). Tuple writes pass `after: tuple`, deletes pass `before: tuple`; attribute writes read the current bag first as `before`. The manager owns the uow lifetime; `AuditedWritePath` sequences the data write + `change_log` + epoch bump on it; the manager commits — all atomic. Schema is per-store; the schema audit is recorded against a per-store bookkeeping tenant `(store, store)`, which the manager seeds if absent.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `WriteTuplesAsync(actor, [tuple])` | tuple persisted; one `change_log` row (actor, op `write`); epoch = 1 |
| write then `DeleteTuplesAsync` | tuple gone; a `delete` audit with the before image; epoch = 2 (one bump per call) |
| `ReadTuplesAsync(ObjectType filter)` | returns the matching tuple |
| `WriteAttributesAsync` then repeat write | attrs persisted; actor + epoch audited; before/after captured on the repeat write |

**Done when:** build clean; all four cases pass (Postgres required).

---

### Task 3: Schema-manager validation + atomic activation

- [ ] **Files:** test `…Tests/Managers/SchemaManagerTests.cs` (no production code).

**Consumes (see README):** `CustodexSchemaManager`.

**Behavior:** a valid schema activates and is readable, with the manager seeding its own bookkeeping tenant (no pre-existing tenant required). An invalid schema (e.g. a permission referencing a non-existent relation) throws `SchemaValidationException` and activates nothing.

**Cases to pin:**

| Setup | Expect |
|---|---|
| valid schema, no pre-existing tenant | activates; readable via `GetActiveSchemaAsync` |
| invalid schema | throws `SchemaValidationException`; nothing activated |

**Done when:** build clean; both cases pass (Postgres required).

---

### Task 4: `UsePostgres` registration

- [ ] **Files:** create `src/Custodex.Storage.Postgres/CustodexPostgresBuilderExtensions.cs`; test `…Tests/Di/UsePostgresTests.cs`.

**Produces:** `CustodexBuilder UsePostgres(this CustodexBuilder, string connectionString)` registering (`TryAddSingleton`): `NpgsqlUnitOfWorkFactory` (+ `IUnitOfWorkFactory`); the four Npgsql stores (+ their interfaces); `IConditionEvaluator` defaulting to **`CelConditionEvaluator`**; `PostgresCacheStore` as `ICacheStore`; `AuditedWritePath`; the four managers (+ their interfaces); and `NpgsqlCteAuthorizer` as `IAuthorizer` (the CTE primary path, registered directly).
**Consumes (see README):** `CustodexBuilder` (Task 1, in Core); the Postgres stores/managers; `IConditionEvaluator`/`CelConditionEvaluator` (M0/06).

**Behavior:** `UsePostgres` lives in `Custodex.Storage.Postgres`, which references Core (for `CustodexBuilder`) and the DI abstractions; Core never references Postgres, so a consumer adds the Postgres package only to call `.UsePostgres(...)`. `TryAddSingleton` means a consumer-registered `IConditionEvaluator` (registered before `UsePostgres`) wins over the default `CelConditionEvaluator`. The authorizer is registered **directly** as `IAuthorizer` — cross-request caching is not enabled here; per-request memoization and read-your-writes still hold. Wrapping `NpgsqlCteAuthorizer` in M0/08's `CachingAuthorizer` is gated on a shared cacheability seam (`CheckInternalAsync` returning `(Allowed, ConditionTouched)`, so only unconditioned results are cached) that the CTE authorizer does not yet expose; once it does, the `IAuthorizer` registration becomes that wrap.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `AddCustodex().UsePostgres(conn).UseSchema(builder)` | resolves `IAuthorizer`, `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager` |
| `AddCustodex().UsePostgres(conn)` | `IAuthorizer` resolves to `NpgsqlCteAuthorizer` (the CTE primary path) |

**Done when:** build clean; both cases pass (Postgres required).

---

### Task 5: `AddCustodexInstrumentation()` OpenTelemetry wiring (in `Custodex.Core`)

- [ ] **Files:** create `src/Custodex.Core/CustodexInstrumentationExtensions.cs`; test in `tests/Custodex.Core.Tests` (`InstrumentationTests`).

**Produces:** `TracerProviderBuilder AddCustodexInstrumentation(this TracerProviderBuilder)` enabling the `"Custodex"` `ActivitySource`, and `MeterProviderBuilder AddCustodexInstrumentation(this MeterProviderBuilder)` enabling the `"Custodex"` `Meter` (both from `CustodexDiagnostics`, M0/01). A consumer chains these into `AddOpenTelemetry().WithTracing(...)`/`WithMetrics(...)`.
**Consumes (see README):** `CustodexDiagnostics` (M0/01); the OpenTelemetry builder types.

**Behavior:** registers the library's source/meter into the existing pipeline; it does not build its own.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `AddCustodexInstrumentation()` + an in-memory exporter, then start a `"Custodex"` activity | the activity is exported |

**Done when:** build clean; the case passes (no Postgres).

---

### Task 6: End-to-end sample

- [ ] **Files:** test `…Tests/Di/EndToEndSampleTests.cs` (no production code).

**Consumes (see README):** the full DI surface (`AddCustodex().UsePostgres().UseSchema()`), the managers, `IAuthorizer`.

**Behavior:** proves the wired-up library works against real Postgres: provision store + tenant (plus the `(store, store)` bookkeeping tenant), activate a schema, write tuples through `IRelationManager` (audited + epoch-bumped), then Check and ListObjects through `IAuthorizer` return the expected answers, and the change log records the actor.

**Cases to pin:**

| Setup | Expect |
|---|---|
| define schema → write tuples → Check | a member granted via a nested group is allowed |
| same → ListObjects | the full set of objects the subject may act on, sorted |
| same → ReadChangeLog | the audit records the write actor |

**Done when:** build clean; the case passes (Postgres required).

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors.
- [ ] `AddCustodex().UsePostgres(conn).UseSchema(builder)` resolves `IAuthorizer` and the four managers (Tasks 1, 4).
- [ ] Managers supply the caller's actor and before/after diffs and call `AuditedWritePath` so the data write + `change_log` + epoch bump commit in one uow (Task 2).
- [ ] An invalid schema throws `SchemaValidationException` and activates nothing; a valid one activates + audits atomically, seeding its bookkeeping tenant (Task 3).
- [ ] `IAuthorizer` resolves to `NpgsqlCteAuthorizer`; the default `IConditionEvaluator` is `CelConditionEvaluator`, overridable by a prior registration (Task 4).
- [ ] `AddCustodexInstrumentation()` subscribes the `"Custodex"` ActivitySource and Meter into OTel (Task 5).
- [ ] The end-to-end sample defines a schema, writes audited tuples, and answers Check + ListObjects over real Postgres (Task 6).
