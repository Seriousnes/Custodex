# M1/07 — Epoch Cache & Change-Audit Wiring

**Goal:** Implement the Postgres `ICacheStore` — the epoch methods (`GetEpochAsync`/`BumpEpochAsync`) over the per-`(store,tenant)` `tenant_epochs` row (M1/01), the bump participating in the supplied unit of work — plus a `cache_entries`-backed `GetAsync`/`SetAsync`. Then provide `AuditedWritePath`, which sequences a data write, its `change_log` entries, and the cache-epoch bump on **one** transaction (spec §6.5 / §9.2). Prove, with Testcontainers, that all three commit atomically and roll back together.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach:** `PostgresCacheStore` reads/writes `tenant_epochs` and `cache_entries` and is `partial` (epoch methods and entry methods split across files). `BumpEpochAsync` runs inside the caller's `IUnitOfWork` via an `INSERT … ON CONFLICT DO UPDATE SET epoch = epoch + 1`, so it commits or rolls back with the data write; `GetEpochAsync` returns `COALESCE(epoch, 0)` so a never-written tenant reads 0. `GetAsync`/`SetAsync` persist a value + epoch stamp + `expires_at` against `cache_entries` for the store's fixed `(store,tenant)` scope, with lazy expiry (an expired row reads as a miss). `AuditedWritePath` is the provider-side in-transaction sequencer: given an open `IUnitOfWork`, it runs the data write + `change_log` append(s) + epoch bump on that same unit of work — atomicity follows from the single transaction. The actor and before/after images are supplied by the caller (spec §6.5); the manager layer (M1/09) assembles them.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints. **No EF Core.** The `tenant_epochs` counter is distinct from the per-row `cache_entries.epoch` stamp (README post-dispatch reconciliation 3).

**Dependencies:** builds on `m0/01` (`ICacheStore`, `CacheEntry`, `IChangeLogStore`, `ChangeLogEntry` — see README), M1/01 (schema incl. `tenant_epochs`, `cache_entries`), M1/03 (UoW), M1/04 (relation/changelog stores, `Json`).

---

### Task 1: `PostgresCacheStore` epoch methods over `tenant_epochs`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/PostgresCacheStore.cs`; test `…Tests/EpochTests.cs`.

**Produces:** `PostgresCacheStore(connectionString, TenantContext scope) : ICacheStore` — `GetEpochAsync(t)` and `BumpEpochAsync(t, uow)`.
**Consumes (see README):** `ICacheStore`, `IUnitOfWork`, `TenantContext`.

**Behavior:** `GetEpochAsync` reads `COALESCE(epoch, 0)` for the tenant on its own connection. `BumpEpochAsync` upserts `+1` on the caller's `IUnitOfWork` (resolved via `NpgsqlUnitOfWork.From`), so it shares the data write's transaction. The epoch methods take the tenant explicitly and ignore the construction scope.

**Cases to pin:**

| Setup | Expect |
|---|---|
| unwritten tenant | reads epoch 0 |
| two bumps, commit | reads 2 |
| one bump, dispose without commit | reads 0 (rolled back with the uow) |

**Done when:** build clean; all three cases pass (Postgres required).

---

### Task 2: `cache_entries` get/set with epoch stamp and lazy expiry

- [ ] **Files:** create `src/Custodex.Storage.Postgres/PostgresCacheStore.Entries.cs`; test `…Tests/CacheEntriesTests.cs`.

**Produces:** `PostgresCacheStore.GetAsync(key)` / `SetAsync(key, entry, ttl)` against `cache_entries` for the store's fixed `(store,tenant)` scope.
**Consumes (see README):** `CacheEntry`.

**Behavior:** `SetAsync` upserts value + epoch + `expires_at = now() + ttl`; `GetAsync` returns the stored `CacheEntry` (value + stamped epoch) and treats a row past `expires_at` as a miss (lazy expiry; M2/05 adds sweeping). The caching layer (M0/08) compares the returned `CacheEntry.Epoch` to the current tenant epoch and treats a mismatch as a miss — this store just persists and returns the stamp.

**Cases to pin:**

| Setup | Expect |
|---|---|
| set then get | value bytes and epoch stamp returned |
| get a missing key | null |
| set with a past ttl, then get | null (expired reads as a miss) |

**Done when:** build clean; all three cases pass (Postgres required); both are working, no throwing stubs.

---

### Task 3: `AuditedWritePath` — data write + change_log + epoch bump in one transaction

- [ ] **Files:** create `src/Custodex.Storage.Postgres/AuditedWritePath.cs`; test `…Tests/AuditedWritePathTests.cs`.

**Produces:** `AuditedWritePath(IRelationStore, IAttributeStore, ISchemaStore, IChangeLogStore, ICacheStore)` with `WriteTuplesAsync(t, actor, add, remove, uow)`, `WriteAttributesAsync(t, actor, obj, before, after, uow)`, and `SetSchemaAsync(store, t, actor, schema, uow)`. Each performs the data write, appends one `change_log` entry per affected target (operations `write`/`delete`/`schema`, before/after images), and bumps the epoch — all on the **same** `uow`. The caller owns commit/rollback and supplies actor + diffs.
**Consumes (see README):** the four store interfaces + `ICacheStore`; `RelationTuple`, `ChangeLogEntry`, `Schema`.

**Behavior** (spec §6.5 / §9.2): atomicity follows from the single transaction, not cross-store coordination. Tuple adds audit as `after: tuple` (op `write`), removes as `before: tuple` (op `delete`); the target string encodes `object#relation@subject`. The audit entry's `Id`/`OccurredAt` are placeholders — the change-log store lets the DB generate `bigserial`/`now()`. One epoch bump per call.

**Cases to pin:**

| Setup | Expect |
|---|---|
| write one tuple via the path, commit | tuple, one `change_log` row (actor, op `write`, target string), and epoch = 1 all present |
| write one tuple, dispose without commit | tuple, `change_log` row, and epoch bump all absent (rolled back as one) |

**Done when:** build clean; both cases pass (Postgres required); commit persists all three and rollback discards all three because they share one transaction.

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors.
- [ ] `GetEpochAsync` returns 0 for an unwritten tenant; `BumpEpochAsync` increments and participates in the caller's uow (rolls back with it).
- [ ] `cache_entries` get/set round-trip value + epoch stamp; expired rows read as a miss; both working.
- [ ] The atomicity test proves tuple write + `change_log` row + epoch bump commit together and roll back together on one `IUnitOfWork`.
- [ ] Every audit entry carries the caller-supplied actor; the path never invents identity.
