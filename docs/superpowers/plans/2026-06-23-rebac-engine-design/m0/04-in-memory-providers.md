# M0/04 — In-Memory Storage Providers

**Goal:** A thread-safe, in-memory implementation of every storage provider interface — `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `ICacheStore`, `IChangeLogStore` — plus a no-op `IUnitOfWork`/`IUnitOfWorkFactory`, in a new `Custodex.Storage.InMemory` project. These back the fast unit tests for every later M0 plan and serve as the portable reference provider.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** each store keeps its data in plain in-memory dictionaries behind a single per-store lock (simple, correct locking — these providers exist for tests and dev at small scale). Every operation filters on `(store, tenant)` as a hard, non-optional predicate (spec §6.3 — tenant isolation is a core invariant). The unit of work is a no-op: writes apply immediately and `CommitAsync` does nothing, matching the transaction seam (Postgres enlists in a real `DbTransaction`). Cache TTL flows through an injected `TimeProvider`, never `DateTimeOffset.UtcNow`, so expiry tests are deterministic.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly. Project: `Custodex.Storage.InMemory` (NEW — created here and added to `Custodex.slnx`).

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/01 (`Custodex.Abstractions`) — see README.

---

### Task 1: Create the project and the no-op unit of work

- [ ] **Files:** create `src/Custodex.Storage.InMemory` (project + add to `Custodex.slnx`), `src/Custodex.Storage.InMemory/NoOpUnitOfWork.cs`, and the test project. Test: `tests/Custodex.Storage.InMemory.Tests/UnitOfWorkTests.cs`.

**Produces:** `NoOpUnitOfWork : IUnitOfWork`, `NoOpUnitOfWorkFactory : IUnitOfWorkFactory`.
**Consumes (see README):** `IUnitOfWork`, `IUnitOfWorkFactory`.

**Behavior:** the factory yields a committable, disposable no-op unit of work; `CommitAsync` and `DisposeAsync` never throw. These are the names every later M0 test wires through.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `BeginAsync` then `CommitAsync` then dispose | no throw; the value is an `IUnitOfWork` |

**Done when:** build clean under TreatWarningsAsErrors; the project is in the solution; the no-op cycle works.

---

### Task 2: In-memory `IRelationStore`

- [ ] **Files:** create `src/Custodex.Storage.InMemory/InMemoryRelationStore.cs`. Test: `tests/Custodex.Storage.InMemory.Tests/RelationStoreTests.cs`.

**Produces:** `InMemoryRelationStore : IRelationStore` — `GetByObjectAsync`, `GetBySubjectAsync`, `ListObjectIdsAsync`, and the batched `WriteAsync(add, remove, uow)`.
**Consumes (see README):** `IRelationStore`, `RelationTuple`, `EntityRef`, `SubjectRef`, `TenantContext`, `IUnitOfWork`.

**Behavior:**
- Tuple identity for add/remove cannot use `RelationTuple` record equality, because `ConditionRef.Parameters` is an `IReadOnlyDictionary` compared by reference. Identity is `(Object, Relation, Subject including its Relation, Condition?.Name)`, so two logically-identical conditioned tuples with distinct dictionary instances still match. Writes are idempotent on that identity.
- `GetBySubjectAsync` matches the full subject including its `Relation` (a subject-set `group:vets#member` is distinct from `group:vets`). `ListObjectIdsAsync` returns the distinct object ids of the given type within the tenant (the type universe the ListObjects oracle and wildcard grants need). Every read and write filters on `(store, tenant)`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| write two members, read by object+relation | both subjects returned |
| add then remove a conditioned tuple via a different dictionary instance | tuple removed (identity ignores parameter identity) |
| read by subject | tuples where that exact subject appears |
| write the same tuple twice | one tuple (idempotent) |
| read from another tenant | empty (no cross-tenant leak) |

**Done when:** build clean; identity, idempotence, and tenant isolation hold.

---

### Task 3: In-memory `ISchemaStore` and `IAttributeStore`

- [ ] **Files:** create `src/Custodex.Storage.InMemory/InMemorySchemaStore.cs`, `…/InMemoryAttributeStore.cs`. Tests: `…Tests/SchemaStoreTests.cs`, `…Tests/AttributeStoreTests.cs`.

**Produces:** `InMemorySchemaStore : ISchemaStore`, `InMemoryAttributeStore : IAttributeStore`.
**Consumes (see README):** `ISchemaStore`, `IAttributeStore`, `Schema`, `EntityRef`, `TenantContext`, `IUnitOfWork`.

**Behavior:**
- `ISchemaStore` is keyed by `store` only (a schema is per-store, not per-tenant); `SetActiveAsync` replaces the active schema. `IAttributeStore` is keyed by `(store, tenant, objectType, objectId)`; `SetAsync` replaces the bag and stores a defensive copy, and `GetAsync` returns a defensive copy, so callers cannot mutate stored state. A missing store/object returns null.

**Cases to pin:**

| Setup | Expect |
|---|---|
| get a never-set store/object | null |
| set then get a schema / attribute bag | round-trips the version / value |
| set twice | latest wins |
| mutate the caller's bag after `SetAsync` | stored value unaffected |
| read attributes from another tenant | null (no leak) |

**Done when:** build clean; defensive copies and tenant isolation hold.

---

### Task 4: In-memory `IChangeLogStore`

- [ ] **Files:** create `src/Custodex.Storage.InMemory/InMemoryChangeLogStore.cs`. Test: `…Tests/ChangeLogStoreTests.cs`.

**Produces:** `InMemoryChangeLogStore : IChangeLogStore`.
**Consumes (see README):** `IChangeLogStore`, `ChangeLogEntry`, `ChangeLogFilter`, `TenantContext`, `IUnitOfWork`.

**Behavior:**
- The store assigns the sequential `long Id` on append (the caller's `ChangeLogEntry.Id` is ignored); ids start at 1 and increase per `(store, tenant)`. `ReadAsync` returns newest-first (descending id), honours `ChangeLogFilter.Since`/`Actor`, and caps at `Limit`. All operations filter on `(store, tenant)`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| append two entries | ids `[2, 1]` newest-first |
| filter by actor / by since / by limit | honoured |
| read from another tenant | empty (no leak) |

**Done when:** build clean; sequential ids, ordering, filtering, and isolation hold.

---

### Task 5: In-memory `ICacheStore` with epoch and deterministic TTL

- [ ] **Files:** create `src/Custodex.Storage.InMemory/InMemoryCacheStore.cs`. Test: `…Tests/CacheStoreTests.cs`.

**Produces:** `InMemoryCacheStore : ICacheStore`, constructed with an optional `TimeProvider` (default `TimeProvider.System`).
**Consumes (see README):** `ICacheStore`, `CacheEntry`, `TenantContext`, `IUnitOfWork`.

**Behavior:**
- Stores `CacheEntry(byte[] Value, long Epoch)` by key with an expiry derived from the injected `TimeProvider`, so expiry is deterministic in tests. `CacheEntry.Epoch` round-trips unchanged (the caching layer in m0/08 compares it to the current tenant epoch). The per-`(store, tenant)` epoch starts at 0; `BumpEpochAsync` increments it; `GetEpochAsync` reads it. An expired entry reads back as null (lazy expiry on read).

**Cases to pin:**

| Setup | Expect |
|---|---|
| set then get | value and epoch round-trip |
| get a missing key | null |
| advance the clock past the TTL | reads null; before the TTL, reads the entry |
| bump the epoch twice | `GetEpochAsync` → 2 (starts at 0) |

**Done when:** build clean; TTL is deterministic via the injected `TimeProvider`; epoch increments.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] Every provider interface has an in-memory implementation; `NoOpUnitOfWork`/`NoOpUnitOfWorkFactory` are present.
- [ ] Every read and write filters on `(store, tenant)`; a cross-tenant no-leak test exists for the relation, attribute, and change-log stores.
- [ ] Relation add/remove identity excludes `ConditionRef.Parameters`; `ListObjectIdsAsync` returns the distinct type universe.
- [ ] Cache TTL uses an injected `TimeProvider`; no `DateTimeOffset.UtcNow` in the implementation.
- [ ] Change-log ids are store-assigned, sequential per `(store, tenant)`, and reads honour the filter.
