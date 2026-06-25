# M2/05 — Hardening the UNLOGGED Postgres Cache

**Goal:** Harden the existing `PostgresCacheStore` (built in m1/07 over the `UNLOGGED` `cache_entries` table with epoch + get/set + lazy expiry) for production across horizontally scaled instances. Add (1) a **per-tenant scoping fix** via a `PostgresCacheStoreFactory` yielding a correctly scoped store per `TenantContext`; (2) a **background TTL sweep** (`CacheSweep` + `CacheSweepService`) deleting expired rows on an interval, complementing the lazy read-time expiry; and (3) a guard test proving the m0/08 `CacheValueCodec` round-trips a `bool` decision through `SetAsync`/`GetAsync`.

**For implementers:** drive this with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each `### Task` is one TDD unit — Red → Green → one Conventional-Commit with the co-author trailer (see `../README.md` → Global Constraints). Tasks are tracked with `- [ ]` checkboxes.

**Architecture/approach:** m1/07 left `PostgresCacheStore(connectionString, scope)` correct for epochs and entries but scoped to a single `(store, tenant)` at construction — wrong for a multi-tenant process registering one cache in DI. A `PostgresCacheStoreFactory(connectionString)` yields a store scoped to a given tenant, so each request resolves its own; the underlying `cache_entries` rows stay keyed by `(store_id, tenant_id, key)` so two store instances for the same tenant share rows (a shared cache across instances). The epoch methods already take an explicit `TenantContext`, so only the `key`-only get/set needed the scope, and the factory supplies it. `CacheSweepService : BackgroundService` periodically runs a whole-table delete of rows past `expires_at` (the `ix_cache_entries_expiry` index from m1/01 makes it cheap), keeping the `UNLOGGED` table from accumulating dead rows that lazy expiry alone never reclaims. `CacheValueCodec` (m0/08) stays the value boundary: `CachingAuthorizer` encodes the `bool` before `SetAsync` and decodes after `GetAsync`; this plan keeps the store value-opaque (`byte[]`) and proves the codec round-trips through it.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, `Microsoft.Extensions.Hosting.Abstractions` (`BackgroundService`), xUnit, Shouldly, Testcontainers.PostgreSql.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/01 (`ICacheStore`, `CacheEntry`); m0/08 (`CacheValueCodec`, `CachingAuthorizer` — the value boundary); m1/01 (`cache_entries` UNLOGGED table + `ix_cache_entries_expiry`, `MigrationRunner`, `PostgresFixture`); m1/03 (`NpgsqlUnitOfWorkFactory`/`NpgsqlUnitOfWork` for seeding); m1/07 (the existing `PostgresCacheStore` — **do not rebuild it**; this plan only adds the factory, sweep, and codec guard around it).

## Shared decisions

- **The existing `PostgresCacheStore` is kept as-is.** m1/07's epoch get/bump and `cache_entries` get/set (with epoch stamp + lazy expiry) are correct and untouched. This plan only **adds** the factory, sweep, service, and codec guard.
- **Per-tenant scoping is solved by a factory, not by changing `ICacheStore` signatures** (which m0/08's `CachingAuthorizer` already calls with the `key`-only get/set and the `TenantContext`-carrying epoch methods).
- **The sweep is whole-table.** Expired rows across all tenants are reclaimed in one `DELETE`; the `expires_at` index makes it index-only on the predicate. The sweep is correctness-neutral — lazy expiry already hides expired rows from reads — it is purely space reclamation.
- **`CacheValueCodec` stays in m0/08.** The Postgres store remains value-opaque; this plan documents and tests the boundary, not moves the codec.

---

### Task 1: `PostgresCacheStoreFactory` — per-tenant scoped stores over shared rows

- [ ] **Files:** create `src/Custodex.Storage.Postgres/PostgresCacheStoreFactory.cs`; test `…Tests/Cache/PostgresCacheStoreFactoryTests.cs`.

**Produces:** `PostgresCacheStoreFactory(string connectionString)` with `ICacheStore For(TenantContext)` returning a `PostgresCacheStore` scoped to that tenant. The factory is the process-wide DI singleton; per-request code calls `For(tenant)`.
**Consumes (see README):** `PostgresCacheStore(connectionString, scope)` (m1/07), `TenantContext`, `ICacheStore` (m0/01).

**Behavior:** m1/07's store binds `(store, tenant)` at construction so the `key`-only get/set can hard-filter on the tenant (defence in depth, spec §6.3). Registering one such store process-wide would pin every tenant's reads to one tenant's rows. The factory keeps the per-tenant hard filter while a single registration serves all tenants — the standard "scoped resource from a singleton factory" shape, with no change to the locked `ICacheStore` contract. Because `cache_entries` rows are keyed by `(store_id, tenant_id, key)`, two stores built for the same tenant read/write the same rows (a shared cache across instances/nodes).

**Cases to pin:**

| Setup | Expect |
|---|---|
| two `For(t)` instances for the same tenant; writer sets `k`, reader gets `k` | reader sees the value + epoch (shared rows) |
| `For(a)` sets `k`; `For(b)` gets `k` (different tenants) | null — tenant isolation |

**Done when:** build clean; cases pass; requires Docker.

---

### Task 2: `CacheSweep` — delete expired rows

- [ ] **Files:** create `src/Custodex.Storage.Postgres/CacheSweep.cs`; test `…Tests/Cache/CacheSweepTests.cs`.

**Produces:** `static Task<int> CacheSweep.RunAsync(connectionString, ct)` — deletes every `cache_entries` row whose `expires_at <= now()` across all tenants and returns the rows reclaimed. Isolated from the hosted service so it is testable directly.
**Consumes (see README):** `cache_entries` + `ix_cache_entries_expiry` (m1/01).

**Behavior:** lazy expiry (m1/07: get filters `expires_at > now()`) hides expired rows from reads but never deletes them; on an `UNLOGGED` table under write churn that leaks space and bloats the index. The sweep reclaims it. KEY DECISION: sweeping is **correctness-neutral** — a swept row was already invisible to reads, so a concurrent reader never observes a difference. No tenant filter is needed (expiry is global). Opens its own short-lived connection.

**Cases to pin:**

| Setup | Expect |
|---|---|
| one live row (10 min ttl) + one already-expired row (negative ttl), both physically present | sweep reclaims ≥1; row count drops to 1; live row still readable, dead row null |
| only a live row | sweep reclaims nothing; row count unchanged |

**Done when:** build clean; cases pass; requires Docker.

---

### Task 3: `CacheSweepService` — the background TTL sweep job

- [ ] **Files:** modify `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj` (add `Microsoft.Extensions.Hosting.Abstractions`); create `src/Custodex.Storage.Postgres/CacheSweepOptions.cs` and `…/CacheSweepService.cs`; add a `Custodex.cache.swept` counter to `src/Custodex.Abstractions/CustodexDiagnostics.cs`; test `…Tests/Cache/CacheSweepServiceTests.cs`.

**Produces:** `CacheSweepOptions { required string ConnectionString; TimeSpan Interval = 5 min }` and `CacheSweepService(CacheSweepOptions) : BackgroundService` running the sweep every `Interval` until the host stops, recording each sweep's reclaimed-row count to a new `CustodexDiagnostics.CacheSwept` counter (spec §11.4 observability).
**Consumes (see README):** `CacheSweep.RunAsync` (Task 2); `BackgroundService`/`IHostedService` from `Microsoft.Extensions.Hosting.Abstractions` (MIT — permitted); `CustodexDiagnostics.Meter` (m0/01).

**Behavior:** the service is opt-in — registered via m1/09's DI extension only when automatic sweeping is wanted; the store stays correct without it (lazy expiry keeps reads right), the service only reclaims space. KEY DECISIONS:
- **The first sweep runs immediately on start** (a `do { sweep } while (await timer.WaitForNextTickAsync)` body executes before the first tick), so a tight test interval reclaims promptly while production uses the 5-minute default.
- Host-stop cancellation breaks the loop cleanly (a cancelled tick wait or sweep is not an error).
- Only positive reclaim counts are added to the counter. Adding an instrument to the existing `"Custodex"` Meter is additive — no contract type changes.

**Cases to pin:**

| Setup | Expect |
|---|---|
| one already-expired row; start the service with a ~50 ms interval; poll (bounded) until row count hits 0; stop the service | row count reaches 0 — the periodic sweep reclaimed it |

**Done when:** build clean; case passes; the service sweeps on its interval, stops with the host, and feeds `Custodex.cache.swept`; requires Docker.

---

### Task 4: `CacheValueCodec` round-trip through the Postgres store

- [ ] **Files:** ensure `tests/Custodex.Storage.Postgres.Tests` references `Custodex.Core` (idempotent); test `…Tests/Cache/PostgresCacheCodecTests.cs` — no new production code.

**Produces:** a guard test proving the m0/08 `CacheValueCodec` round-trips a `bool` decision through `PostgresCacheStore.SetAsync`/`GetAsync` (the value boundary `CachingAuthorizer` owns) and that an **epoch mismatch reads as a miss** at the caching-layer level.
**Consumes (see README):** `CacheValueCodec` (`Custodex.Core.Caching`, m0/08), `PostgresCacheStore` (m1/07).

**Behavior — the integration being pinned:** `CacheValueCodec` lives in `Custodex.Core` and the store is value-opaque (`byte[]`), so neither side alone proves the end-to-end value boundary over a real database. This wires `Encode → SetAsync → GetAsync → Decode` against Testcontainers Postgres. KEY DECISION: an epoch-mismatch entry is **physically present but stale** — sweep/lazy-expiry are TTL, not epoch; the caching layer (m0/08 `CachingAuthorizer`) compares the entry's stamp to the current tenant epoch and treats a mismatch as a miss. The test stamps an entry at the current epoch, bumps the epoch (as the audited write path does), and asserts the entry is still present but its stamp no longer equals the current epoch.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `Encode(true)` → `SetAsync` at current epoch → `GetAsync` → `Decode` | decodes to `true` (survives the DB round-trip) |
| `SetAsync` at epoch E; bump epoch; `GetAsync` | entry present; its `Epoch` != current epoch (caching layer treats as a miss) |

**Done when:** build clean; cases pass; requires Docker.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [ ] The existing m1/07 `PostgresCacheStore` is unchanged — this plan only adds the factory, sweep, service, codec test, and one diagnostics counter.
- [ ] `PostgresCacheStoreFactory.For(tenant)` yields a tenant-scoped store; same-tenant instances share rows; different tenants are isolated (Task 1).
- [ ] `CacheSweep.RunAsync` deletes expired rows and leaves live ones (Task 2).
- [ ] `CacheSweepService` sweeps on its interval (first sweep immediate) and stops with the host; reclaimed rows feed `Custodex.cache.swept` (Task 3).
- [ ] `CacheValueCodec` round-trips a `bool` through the Postgres store; an epoch-mismatch entry is recognized as a miss by the stamp comparison (Task 4).

## Contract gaps (reported, not changed)

- **`CustodexDiagnostics.CacheSwept` counter (additive).** Task 3 adds a `Custodex.cache.swept` `Counter<long>` to the existing m0/01 `CustodexDiagnostics` holder for the spec §11.4 cache observability surface. Additive on the existing `"Custodex"` Meter, not a change to any contract record/interface; no README change required.
- **Hosting dependency on `Microsoft.Extensions.Hosting.Abstractions` (MIT).** `CacheSweepService : BackgroundService` adds this Microsoft package to `Custodex.Storage.Postgres`; MIT-licensed (permitted) and the service is opt-in. No README change; recorded for the packaging review.
