# M1/03 — Unit of Work & Transaction Enlistment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `NpgsqlUnitOfWork : IUnitOfWork` and `NpgsqlUnitOfWorkFactory : IUnitOfWorkFactory` over `NpgsqlConnection`/`NpgsqlTransaction`, with two ownership modes — **owned** (the factory opens and commits its own connection+transaction) and **supplied** (the unit of work enlists in an externally provided `DbConnection`+`DbTransaction`, so engine writes commit inside the consuming application's own transaction; spec §6.6 / §9.2). Prove, with Testcontainers, atomic commit, atomic rollback, and correct enlistment in an external transaction that the engine must not commit, roll back, or close.

**Architecture:** `NpgsqlUnitOfWork` carries an **ownership flag**. When owned, `CommitAsync` commits and `DisposeAsync` rolls back if not yet committed, then disposes the transaction and connection it owns. When supplied, `CommitAsync` is a **no-op** (the external owner commits) and `DisposeAsync` does **not** touch the external connection/transaction — it must leave them open and usable. The stores in m1/04 and m1/07 obtain the live `NpgsqlConnection` and `NpgsqlTransaction` by casting the `IUnitOfWork` they are handed to `NpgsqlUnitOfWork` and reading its `Connection`/`Transaction` properties. Enlistment is exposed as `NpgsqlUnitOfWorkFactory.Enlist(DbConnection, DbTransaction)` — a provider extension beyond the `IUnitOfWorkFactory` interface (which only declares `BeginAsync`).

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. Depends on `m0/01` (`IUnitOfWork`, `IUnitOfWorkFactory`) and `m1/01` (the schema + `PostgresFixture`). **No EF Core.**

## Shared decisions (locked — used verbatim by m1/04, m1/07)

- A store receives an `IUnitOfWork` on its write path and obtains live ADO.NET handles by `((NpgsqlUnitOfWork)uow)` and reading `.Connection` / `.Transaction`. A helper `NpgsqlUnitOfWork.From(IUnitOfWork)` performs the cast with a clear throw on the wrong type.
- **Owned** unit of work: created by `NpgsqlUnitOfWorkFactory.BeginAsync`; it owns conn+tx; `CommitAsync` commits; `DisposeAsync` rolls back uncommitted work and disposes both.
- **Supplied** unit of work: created by `NpgsqlUnitOfWorkFactory.Enlist(conn, tx)`; it borrows conn+tx; `CommitAsync` is a no-op; `DisposeAsync` releases nothing it does not own. The external owner is solely responsible for commit/rollback/close.
- A supplied `DbConnection` that is not an `NpgsqlConnection` (or `DbTransaction` that is not `NpgsqlTransaction`) is rejected with a clear exception — jsonb mapping requires the Npgsql types.

---

### Task 1: `NpgsqlUnitOfWork` with the ownership flag

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlUnitOfWork.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/NpgsqlUnitOfWorkTests.cs`

**Interfaces:**
- Produces: `NpgsqlUnitOfWork : IUnitOfWork` exposing `NpgsqlConnection Connection`, `NpgsqlTransaction Transaction`, `Task CommitAsync(CancellationToken)`, `ValueTask DisposeAsync()`, and the static `NpgsqlUnitOfWork.From(IUnitOfWork)` accessor. Two internal constructors back the owned/supplied modes; the factory (Task 2) is the public door.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Storage.Postgres.Tests/NpgsqlUnitOfWorkTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

public class NpgsqlUnitOfWorkTests
{
    private sealed class FakeUow : IUnitOfWork
    {
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void From_rejects_a_non_npgsql_unit_of_work()
    {
        Should.Throw<InvalidOperationException>(() => NpgsqlUnitOfWork.From(new FakeUow()));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter NpgsqlUnitOfWorkTests`
Expected: FAIL — `NpgsqlUnitOfWork` does not exist.

- [ ] **Step 3: Implement the unit of work**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlUnitOfWork.cs
using Npgsql;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// A Postgres unit of work. In <b>owned</b> mode it owns the connection and transaction and
/// commits/rolls them back. In <b>supplied</b> mode it borrows an external connection and
/// transaction supplied by the consuming application: <see cref="CommitAsync"/> is a no-op and
/// disposal releases nothing the external owner still controls (spec §6.6 / §9.2).
/// </summary>
public sealed class NpgsqlUnitOfWork : IUnitOfWork
{
    private readonly bool _owned;
    private bool _committed;
    private bool _disposed;

    private NpgsqlUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction, bool owned)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    internal static NpgsqlUnitOfWork Owned(NpgsqlConnection connection, NpgsqlTransaction transaction)
        => new(connection, transaction, owned: true);

    internal static NpgsqlUnitOfWork Supplied(NpgsqlConnection connection, NpgsqlTransaction transaction)
        => new(connection, transaction, owned: false);

    /// <summary>Resolves the live Npgsql handles from an <see cref="IUnitOfWork"/> a store was handed.</summary>
    public static NpgsqlUnitOfWork From(IUnitOfWork uow)
        => uow as NpgsqlUnitOfWork
           ?? throw new InvalidOperationException(
               $"Expected an {nameof(NpgsqlUnitOfWork)} but got {uow.GetType().Name}. " +
               "The Postgres provider requires a Postgres unit of work.");

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (!_owned)
            return;   // The external owner commits in supplied mode.

        await Transaction.CommitAsync(ct);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (!_owned)
            return;   // Never touch the external owner's connection/transaction.

        if (!_committed)
            await Transaction.RollbackAsync();

        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter NpgsqlUnitOfWorkTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add NpgsqlUnitOfWork with owned/supplied ownership flag"
```

---

### Task 2: `NpgsqlUnitOfWorkFactory` — owned `BeginAsync` and supplied `Enlist`

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlUnitOfWorkFactory.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/UnitOfWorkFactoryTests.cs`

**Interfaces:**
- Produces: `NpgsqlUnitOfWorkFactory : IUnitOfWorkFactory` with `Task<IUnitOfWork> BeginAsync(CancellationToken)` (owned: opens a fresh `NpgsqlConnection` from the configured connection string and begins a transaction) and `IUnitOfWork Enlist(DbConnection connection, DbTransaction transaction)` (supplied: validates the handles are Npgsql and wraps them without owning them).

- [ ] **Step 1: Write the failing test** (uses a real container so `BeginAsync` can open a connection)

```csharp
// tests/Custodex.Storage.Postgres.Tests/UnitOfWorkFactoryTests.cs
using System.Data.Common;
using Npgsql;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class UnitOfWorkFactoryTests(PostgresFixture fx)
{
    [Fact]
    public async Task BeginAsync_returns_an_owned_unit_of_work_with_live_handles()
    {
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        await using var uow = await factory.BeginAsync();

        var npg = NpgsqlUnitOfWork.From(uow);
        npg.Connection.State.ShouldBe(System.Data.ConnectionState.Open);
        npg.Transaction.ShouldNotBeNull();
    }

    [Fact]
    public void Enlist_rejects_a_non_npgsql_connection()
    {
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        DbConnection fake = new System.Data.SqlClient.SqlConnection();
        Should.Throw<InvalidOperationException>(() => factory.Enlist(fake, null!));
    }
}
```

> The `System.Data.SqlClient` reference is only to construct a wrong-typed `DbConnection` for the negative test. Add it to the test project: `dotnet add tests/Custodex.Storage.Postgres.Tests package System.Data.SqlClient`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter UnitOfWorkFactoryTests`
Expected: FAIL — `NpgsqlUnitOfWorkFactory` does not exist.

- [ ] **Step 3: Implement the factory**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlUnitOfWorkFactory.cs
using System.Data.Common;
using Npgsql;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

public sealed class NpgsqlUnitOfWorkFactory(string connectionString) : IUnitOfWorkFactory
{
    private readonly string _connectionString = connectionString;

    /// <summary>Owned mode: open a fresh connection and begin a transaction the engine commits.</summary>
    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        return NpgsqlUnitOfWork.Owned(connection, transaction);
    }

    /// <summary>
    /// Supplied mode (spec §6.6 / §9.2): enlist in the consuming application's own connection and
    /// transaction so engine writes commit atomically inside its unit of work. The engine never
    /// commits, rolls back, or closes these handles.
    /// </summary>
    public IUnitOfWork Enlist(DbConnection connection, DbTransaction transaction)
    {
        if (connection is not NpgsqlConnection npgConn)
            throw new InvalidOperationException(
                $"The Postgres provider requires an {nameof(NpgsqlConnection)}, got {connection.GetType().Name}.");
        if (transaction is not NpgsqlTransaction npgTx)
            throw new InvalidOperationException(
                $"The Postgres provider requires an {nameof(NpgsqlTransaction)}, got {transaction.GetType().Name}.");

        return NpgsqlUnitOfWork.Supplied(npgConn, npgTx);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter UnitOfWorkFactoryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add NpgsqlUnitOfWorkFactory with owned BeginAsync and supplied Enlist"
```

---

### Task 3: Atomic commit and rollback of an owned unit of work

**Files:**
- Test: `tests/Custodex.Storage.Postgres.Tests/OwnedTransactionTests.cs`

**Interfaces:**
- Consumes: `NpgsqlUnitOfWorkFactory`, `MigrationRunner`, `PostgresFixture`. No production code changes — these tests pin the owned-mode commit/rollback semantics that m1/04 and m1/07 rely on.

> This task writes through `relation_tuples` directly via Dapper so it is independent of the store implementations (which arrive in m1/04). It needs a store and tenant row first because of the FK.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/OwnedTransactionTests.cs
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class OwnedTransactionTests(PostgresFixture fx)
{
    private const string InsertTuple = """
        INSERT INTO relation_tuples
            (store_id, tenant_id, object_type, object_id, relation, subject_type, subject_id)
        VALUES (@store, @tenant, 'animal', @oid, 'medicator', 'user', 'dr-smith')
        """;

    private static async Task SeedTenantAsync(NpgsqlUnitOfWork uow, string store, string tenant)
    {
        await uow.Connection.ExecuteAsync(
            "INSERT INTO stores (id) VALUES (@store) ON CONFLICT DO NOTHING",
            new { store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@store, @tenant) ON CONFLICT DO NOTHING",
            new { store, tenant }, uow.Transaction);
    }

    [Fact]
    public async Task Commit_persists_the_write()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        const string store = "owned-commit";
        const string tenant = "t1";

        await using (var u = await factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await SeedTenantAsync(uow, store, tenant);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "EL-001" }, uow.Transaction);
            await u.CommitAsync();
        }

        await using var verify = await fx.OpenAsync();
        var count = await verify.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM relation_tuples WHERE store_id = @store AND object_id = 'EL-001'",
            new { store });
        count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispose_without_commit_rolls_back_the_write()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        const string store = "owned-rollback";
        const string tenant = "t1";

        // Seed tenant in its own committed uow so the FK exists regardless of the rollback below.
        await using (var u = await factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await SeedTenantAsync(uow, store, tenant);
            await u.CommitAsync();
        }

        await using (var u = await factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "EL-002" }, uow.Transaction);
            // No CommitAsync → DisposeAsync rolls back.
        }

        await using var verify = await fx.OpenAsync();
        var count = await verify.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM relation_tuples WHERE store_id = @store AND object_id = 'EL-002'",
            new { store });
        count.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter OwnedTransactionTests`
Expected: PASS — `NpgsqlUnitOfWork` from Task 1 already implements owned commit/rollback. (If a test fails, fix `NpgsqlUnitOfWork`, not the test — the test is the spec.)

- [ ] **Step 3: Commit**

```bash
git add tests/Custodex.Storage.Postgres.Tests
git commit -m "test: prove owned unit of work commits and rolls back atomically"
```

---

### Task 4: Enlistment in an external transaction (supplied mode)

**Files:**
- Test: `tests/Custodex.Storage.Postgres.Tests/EnlistmentTests.cs`

**Interfaces:**
- Consumes: `NpgsqlUnitOfWorkFactory.Enlist`. Proves the engine write is invisible to a second connection until the **external** owner commits, visible after, gone if the external owner rolls back — and that the external connection stays open and usable after the supplied unit of work is disposed.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/EnlistmentTests.cs
using Dapper;
using Npgsql;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class EnlistmentTests(PostgresFixture fx)
{
    private const string InsertTuple = """
        INSERT INTO relation_tuples
            (store_id, tenant_id, object_type, object_id, relation, subject_type, subject_id)
        VALUES (@store, @tenant, 'animal', @oid, 'medicator', 'user', 'dr-smith')
        """;

    private static async Task<long> CountAsync(NpgsqlConnection conn, string store, string oid) =>
        await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM relation_tuples WHERE store_id = @store AND object_id = @oid",
            new { store, oid });

    private async Task SeedTenantAsync(string store, string tenant)
    {
        await using var conn = await fx.OpenAsync();
        await conn.ExecuteAsync("INSERT INTO stores (id) VALUES (@store) ON CONFLICT DO NOTHING", new { store });
        await conn.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@store, @tenant) ON CONFLICT DO NOTHING",
            new { store, tenant });
    }

    [Fact]
    public async Task Engine_write_is_invisible_until_the_external_owner_commits()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        const string store = "enlist-commit";
        const string tenant = "t1";
        await SeedTenantAsync(store, tenant);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        // The consuming application's own connection + transaction.
        await using var appConn = await fx.OpenAsync();
        await using var appTx = await appConn.BeginTransactionAsync();

        // The engine enlists and writes a tuple inside the app's transaction.
        await using (var u = factory.Enlist(appConn, appTx))
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "EL-100" }, uow.Transaction);
            await u.CommitAsync();   // no-op in supplied mode
        }

        // A second, independent connection cannot yet see the uncommitted row.
        await using (var other = await fx.OpenAsync())
            (await CountAsync(other, store, "EL-100")).ShouldBe(0);

        // The external owner commits — only now is the engine write durable.
        await appTx.CommitAsync();

        await using (var other = await fx.OpenAsync())
            (await CountAsync(other, store, "EL-100")).ShouldBe(1);

        // The external connection is still open and usable after the supplied uow was disposed.
        appConn.State.ShouldBe(System.Data.ConnectionState.Open);
        (await CountAsync(appConn, store, "EL-100")).ShouldBe(1);
    }

    [Fact]
    public async Task Engine_write_is_discarded_when_the_external_owner_rolls_back()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        const string store = "enlist-rollback";
        const string tenant = "t1";
        await SeedTenantAsync(store, tenant);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using var appConn = await fx.OpenAsync();
        var appTx = await appConn.BeginTransactionAsync();

        await using (var u = factory.Enlist(appConn, appTx))
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "EL-200" }, uow.Transaction);
            await u.CommitAsync();   // no-op
        }

        // The external owner rolls back — disposing the supplied uow must not have committed.
        await appTx.RollbackAsync();
        await appTx.DisposeAsync();

        await using var other = await fx.OpenAsync();
        (await CountAsync(other, store, "EL-200")).ShouldBe(0);

        // The external connection survives and remains usable.
        appConn.State.ShouldBe(System.Data.ConnectionState.Open);
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter EnlistmentTests`
Expected: PASS — supplied mode commits nothing of its own and disposes nothing of the external owner's. (If a test fails, the bug is in `NpgsqlUnitOfWork` supplied-mode handling, not in the test.)

- [ ] **Step 3: Commit**

```bash
git add tests/Custodex.Storage.Postgres.Tests
git commit -m "test: prove engine enlists in an external transaction without owning it"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Owned mode: `CommitAsync` persists; disposing without commit rolls back; conn+tx are disposed.
- [ ] Supplied mode: `CommitAsync` is a no-op; disposal does not commit, roll back, or close the external conn/tx; the external connection is `Open` and usable afterwards.
- [ ] The engine write under enlistment is invisible to a second connection until the external owner commits, and is discarded if the external owner rolls back.
- [ ] `Enlist` rejects non-Npgsql `DbConnection`/`DbTransaction` with a clear exception.
- [ ] `NpgsqlUnitOfWork.From` is the single accessor m1/04 and m1/07 use to reach the live handles.
