# M1/03 — Unit of Work & Transaction Enlistment

**Goal:** Implement the Npgsql `IUnitOfWork`/`IUnitOfWorkFactory` over `NpgsqlConnection`/`NpgsqlTransaction` with two ownership modes — **owned** (the factory opens and commits its own connection + transaction) and **supplied** (the unit of work enlists in an externally provided connection + transaction so engine writes commit inside the consuming application's own transaction; spec §6.6 / §9.2). Prove, with Testcontainers, atomic commit, atomic rollback, and correct enlistment in an external transaction the engine must not commit, roll back, or close.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach:** `NpgsqlUnitOfWork` carries an ownership flag. **Owned:** `CommitAsync` commits, `DisposeAsync` rolls back if not yet committed, then disposes the connection + transaction it owns. **Supplied:** `CommitAsync` is a no-op (the external owner commits) and `DisposeAsync` touches nothing it does not own — the external handles stay open and usable. Stores (M1/04, M1/07) obtain the live handles by resolving the `IUnitOfWork` they are handed to `NpgsqlUnitOfWork` via the static `NpgsqlUnitOfWork.From(IUnitOfWork)` accessor (clear throw on the wrong type) and reading its `Connection`/`Transaction`. Enlistment is `NpgsqlUnitOfWorkFactory.Enlist(DbConnection, DbTransaction)` — a provider method beyond the `IUnitOfWorkFactory` interface, which only declares `BeginAsync`. A supplied handle that is not Npgsql is rejected with a clear exception (jsonb mapping needs the Npgsql types).

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on `m0/01` (`IUnitOfWork`, `IUnitOfWorkFactory` — see README) and M1/01 (schema + `PostgresFixture`).

---

### Task 1: `NpgsqlUnitOfWork` with the ownership flag

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlUnitOfWork.cs`; test `…Tests/NpgsqlUnitOfWorkTests.cs`.

**Produces:** `NpgsqlUnitOfWork : IUnitOfWork` exposing `Connection`/`Transaction`, `CommitAsync`, `DisposeAsync`, and the static `From(IUnitOfWork)` accessor. Two internal factory methods back owned/supplied modes; the factory (Task 2) is the public door.
**Consumes (see README):** `IUnitOfWork`.

**Behavior:** owned mode commits/rolls-back and disposes both handles; supplied mode no-ops commit and releases nothing. `From` throws `InvalidOperationException` when handed a non-Npgsql unit of work.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `From(fake IUnitOfWork)` | throws `InvalidOperationException` |

**Done when:** build clean; the case passes (no Postgres).

---

### Task 2: `NpgsqlUnitOfWorkFactory` — owned `BeginAsync` and supplied `Enlist`

- [ ] **Files:** create `src/Custodex.Storage.Postgres/NpgsqlUnitOfWorkFactory.cs`; test `…Tests/UnitOfWorkFactoryTests.cs`.

**Produces:** `NpgsqlUnitOfWorkFactory(connectionString) : IUnitOfWorkFactory` — `BeginAsync` (owned: opens a fresh connection from the configured string and begins a transaction) and `Enlist(DbConnection, DbTransaction)` (supplied: validates the handles are Npgsql and wraps them without owning them).
**Consumes (see README):** `IUnitOfWorkFactory`; `NpgsqlUnitOfWork` (Task 1).

**Behavior:** `Enlist` rejects a non-`NpgsqlConnection`/`NpgsqlTransaction` with a clear exception. The negative test constructs a wrong-typed `DbConnection` (e.g. `System.Data.SqlClient.SqlConnection`) purely to exercise the guard.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `BeginAsync` against the container | returns an owned uow with an open connection and a non-null transaction |
| `Enlist(non-Npgsql connection, …)` | throws `InvalidOperationException` |

**Done when:** build clean; both cases pass (`BeginAsync` needs Postgres).

---

### Task 3: Atomic commit and rollback of an owned unit of work

- [ ] **Files:** test `…Tests/OwnedTransactionTests.cs` (no production code; pins owned-mode semantics M1/04 and M1/07 rely on).

**Consumes (see README):** `NpgsqlUnitOfWorkFactory`, `MigrationRunner`, `PostgresFixture`.

**Behavior:** writes through `relation_tuples` directly via Dapper (independent of the not-yet-built stores), seeding a store + tenant first for the FK. Commit persists; disposing without commit rolls back. If a test fails, the bug is in `NpgsqlUnitOfWork`, not the test.

**Cases to pin:**

| Setup | Expect |
|---|---|
| write a tuple, commit | the row is visible from a fresh connection |
| write a tuple, dispose without commit | the row is absent |

**Done when:** build clean; both cases pass (Postgres required).

---

### Task 4: Enlistment in an external transaction (supplied mode)

- [ ] **Files:** test `…Tests/EnlistmentTests.cs` (no production code).

**Consumes (see README):** `NpgsqlUnitOfWorkFactory.Enlist`.

**Behavior:** the engine enlists in the consuming application's own connection + transaction and writes a tuple; `CommitAsync` is a no-op. The write is invisible to a second connection until the **external** owner commits, visible after, and discarded if the external owner rolls back. After the supplied uow is disposed, the external connection stays open and usable.

**Cases to pin:**

| Setup | Expect |
|---|---|
| engine write, before external commit | invisible to a second connection |
| external owner commits | the write becomes durable; external connection still open |
| external owner rolls back | the write is discarded; external connection still open |

**Done when:** build clean; both cases pass (Postgres required); supplied mode commits nothing of its own and disposes nothing of the owner's.

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors.
- [ ] Owned: commit persists; dispose-without-commit rolls back; both handles disposed.
- [ ] Supplied: commit is a no-op; dispose leaves the external conn/tx open and usable.
- [ ] Enlisted write is invisible until the external owner commits; discarded on its rollback.
- [ ] `Enlist` rejects non-Npgsql handles; `From` is the single accessor M1/04 and M1/07 use to reach the live handles.
