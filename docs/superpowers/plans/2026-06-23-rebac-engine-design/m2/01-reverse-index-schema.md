# M2/01 — Reverse-Index Schema & `IIndexStore` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Define the `IIndexStore` contract members in `Relkit.Abstractions` (intentionally empty in the foundation contract today) and implement `NpgsqlIndexStore` over the `reverse_index` table from `m1/01`, plus the `ReverseIndexRow` value type and the `SubjectKey` encoding the index stores subjects under. This is the storage seam the full rebuild (`m2/02`) and incremental maintenance (`m2/03`) write through, and the index-backed `ListObjects` (`m2/04`) reads through.

**Architecture:** The `reverse_index` table (`m1/01` DDL) holds resolved **structural (unconditioned) grants**: `(store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)`. A row asserts "`subject` holds `permission` on `object_type:object_id` structurally; if `conditioned` is true, a request-time condition was reached on the grant path and must be re-evaluated before the grant is honoured." `subject` is the canonical `SubjectRef` string (`user:alice`, `group:vets#member`, `user:*`). The store is a thin, mechanical CRUD seam over that table — no algebra lives here; the expansion that decides which rows exist lives in `m2/02`/`m2/03`. Every operation hard-filters `(store_id, tenant_id, schema_version)` (spec §6.3 / §7.3).

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 license metadata; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. Depends on `m0/01` (`Relkit.Abstractions`, where `IIndexStore` lives — empty today), `m1/01` (`reverse_index` DDL, `MigrationRunner`, `PostgresFixture`), `m1/03` (`NpgsqlUnitOfWork`/`NpgsqlUnitOfWorkFactory` — index writes enlist in the caller's unit of work so they commit with the data write per spec §7.3/§9.2), `m1/04` (`Json` helper, the `NpgsqlRelationStore`/`NpgsqlSchemaStore` patterns).

## Shared decisions (locked — used verbatim by m2/02, m2/03, m2/04, m2/06)

These M2 plans share the index table and its row shape. Pin them once, here, and reuse them unchanged:

- **`reverse_index` natural key** (the identity an upsert/delete targets) is
  `(store_id, tenant_id, schema_version, subject, permission, object_type, object_id)`.
  `conditioned` is **not** part of the key — it is a mutable attribute of the row, so an unconditioned grant becoming conditioned (or vice versa) is an in-place update, not a new row.
- **`subject` encoding** is the canonical `SubjectRef.ToString()`: `"{Type}:{Id}"` for a concrete or wildcard subject, `"{Type}:{Id}#{Relation}"` for a subject-set. The index stores rows for the **query subjects** `ListObjects` is asked about — concrete users (`user:alice`) and the public wildcard (`user:*`). It does not store rows for intermediate subject-sets; those are expanded away during rebuild/maintenance.
- **A unique index** enforces the natural key so `ON CONFLICT` upserts are well-defined; it is added by this plan's migration `002`.
- **Schema-version stamping**: every row carries the `schema_version` that was active when it was computed. `m1/01`'s `ix_reverse_index_scan` already leads with `(store, tenant, schema_version, subject, permission, object_type)` so the scan and the staleness check share one index.

---

### Task 1: Add the `reverse_index` natural-key unique index (migration 002)

**Files:**
- Create: `src/Relkit.Storage.Postgres/Migrations/002_reverse_index_unique.sql`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexSchemaTests.cs`

**Interfaces:**
- Produces: a unique index `ux_reverse_index_natural` on the locked natural key, so the upsert in Task 3 has a conflict target. Applied by the existing `MigrationRunner` (`m1/01`) — it picks up any embedded `Migrations/*.sql` in name order.

> The `m1/01` `001_initial_schema.sql` created `reverse_index` and the *scan* index `ix_reverse_index_scan`, but not a unique constraint on the natural key (M2 owns the maintenance semantics, so the conflict target lands here). This migration adds it. It is idempotent (`IF NOT EXISTS`) like every other script, so re-running the runner is a no-op.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexSchemaTests.cs
using Dapper;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ReverseIndexSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task Reverse_index_has_the_natural_key_unique_index()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var indexes = (await conn.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'reverse_index'"))
            .ToHashSet();

        indexes.ShouldContain("ux_reverse_index_natural");
        indexes.ShouldContain("ix_reverse_index_scan");   // from m1/01, still present
    }

    [Fact]
    public async Task Natural_key_rejects_a_duplicate_row()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await conn.ExecuteAsync("INSERT INTO stores (id) VALUES ('idx') ON CONFLICT DO NOTHING");
        await conn.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES ('idx','t') ON CONFLICT DO NOTHING");

        const string insert = """
            INSERT INTO reverse_index
              (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
            VALUES ('idx','t','v1','user:alice','edit','species','kangaroo', false)
            """;
        await conn.ExecuteAsync(insert);

        // Same natural key, different conditioned flag => unique violation (conditioned is not in the key).
        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(() => conn.ExecuteAsync(
            insert.Replace("false", "true")));
        ex.SqlState.ShouldBe("23505");   // unique_violation
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReverseIndexSchemaTests`
Expected: FAIL — `ux_reverse_index_natural` does not exist.

- [ ] **Step 3: Write the migration**

```sql
-- src/Relkit.Storage.Postgres/Migrations/002_reverse_index_unique.sql

-- Natural key for reverse_index upserts/deletes (locked in m2/01 shared decisions).
-- conditioned is intentionally NOT in the key: flipping it is an in-place UPDATE.
CREATE UNIQUE INDEX IF NOT EXISTS ux_reverse_index_natural
    ON reverse_index (
        store_id, tenant_id, schema_version, subject, permission, object_type, object_id
    );
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReverseIndexSchemaTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Migrations/002_reverse_index_unique.sql tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexSchemaTests.cs
git commit -m "feat: add reverse_index natural-key unique index migration"
```

---

### Task 2: Define the `IIndexStore` contract and `ReverseIndexRow`

**Files:**
- Modify: `src/Relkit.Abstractions/Storage.cs`
- Create: `src/Relkit.Abstractions/ReverseIndexRow.cs`
- Test: `tests/Relkit.Abstractions.Tests/IndexStoreContractTests.cs`

**Interfaces:**
- Produces: the populated `IIndexStore` interface (empty `{ }` today in `m0/01`) and the `ReverseIndexRow` record. **This is a contract addition** — report it (see this plan's return). `IIndexStore` members:
  - `Task UpsertAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows, IUnitOfWork uow, CancellationToken ct = default)` — insert-or-update each row on the natural key, setting `conditioned`.
  - `Task DeleteForObjectAsync(TenantContext t, string schemaVersion, string objectType, string objectId, IUnitOfWork uow, CancellationToken ct = default)` — remove every row for one object (the unit incremental maintenance rebuilds, `m2/03`).
  - `Task DeleteRowsAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows, IUnitOfWork uow, CancellationToken ct = default)` — remove specific rows by natural key (targeted diffs, `m2/03`).
  - `Task<IReadOnlyList<ReverseIndexRow>> QueryObjectsAsync(TenantContext t, string schemaVersion, string subject, string permission, string objectType, int limit, string? afterObjectId, CancellationToken ct = default)` — the `ListObjects` scan: rows for one `(subject, permission, objectType)`, ordinal-sorted by `object_id`, starting strictly after `afterObjectId`, capped at `limit`.
  - `Task<IReadOnlyList<ReverseIndexRow>> ReadForObjectAsync(TenantContext t, string schemaVersion, string objectType, string objectId, CancellationToken ct = default)` — every row for one object (the "current rows" side of an incremental diff, `m2/03`).
  - `Task ClearAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)` — drop **all** index rows for a (store, tenant) across every schema_version (the start of a full rebuild and the schema-change invalidation, `m2/02`).
  - `Task<bool> IsBuiltAsync(TenantContext t, string schemaVersion, CancellationToken ct = default)` — the `RebuildMarker` read: has the index been built for this (store, tenant, schema_version)? A schema change yields a new version whose marker is absent, so a stale index is never served (spec §7.3).
  - `Task MarkBuiltAsync(TenantContext t, string schemaVersion, IUnitOfWork uow, CancellationToken ct = default)` — the `RebuildMarker` write: record that the index is current for this (store, tenant, schema_version).
- Consumes: `TenantContext`, `IUnitOfWork` from `Relkit.Abstractions`.

> **Why these members.** `UpsertAsync`/`DeleteForObjectAsync`/`QueryObjectsAsync`/`MarkBuiltAsync` (the `RebuildMarkerAsync` role) are the four named in the orchestrator brief; `DeleteRowsAsync`/`ReadForObjectAsync`/`ClearAsync`/`IsBuiltAsync` are the additional primitives the rebuild (`m2/02`) and the exclusion-correct incremental diff (`m2/03`) require. All are `(store, tenant, schema_version)`-scoped. `ReverseIndexRow` carries only the per-row columns (`subject`, `permission`, `object_type`, `object_id`, `conditioned`); the scope is passed alongside so a row value is reusable across schema versions in tests.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Abstractions.Tests/IndexStoreContractTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class IndexStoreContractTests
{
    [Fact]
    public void ReverseIndexRow_carries_its_columns()
    {
        var row = new ReverseIndexRow("user:alice", "edit", "species", "kangaroo", Conditioned: false);
        row.Subject.ShouldBe("user:alice");
        row.Permission.ShouldBe("edit");
        row.ObjectType.ShouldBe("species");
        row.ObjectId.ShouldBe("kangaroo");
        row.Conditioned.ShouldBeFalse();
    }

    [Fact]
    public void IIndexStore_declares_the_maintenance_and_query_members()
    {
        var t = typeof(IIndexStore);
        t.GetMethod("UpsertAsync").ShouldNotBeNull();
        t.GetMethod("DeleteForObjectAsync").ShouldNotBeNull();
        t.GetMethod("DeleteRowsAsync").ShouldNotBeNull();
        t.GetMethod("QueryObjectsAsync").ShouldNotBeNull();
        t.GetMethod("ReadForObjectAsync").ShouldNotBeNull();
        t.GetMethod("ClearAsync").ShouldNotBeNull();
        t.GetMethod("IsBuiltAsync").ShouldNotBeNull();
        t.GetMethod("MarkBuiltAsync").ShouldNotBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter IndexStoreContractTests`
Expected: FAIL — `ReverseIndexRow` not defined and `IIndexStore` has no members.

- [ ] **Step 3: Define `ReverseIndexRow` and populate `IIndexStore`**

```csharp
// src/Relkit.Abstractions/ReverseIndexRow.cs
namespace Relkit.Abstractions;

/// <summary>
/// One maintained reverse-index row: <paramref name="Subject"/> (a canonical SubjectRef string)
/// holds <paramref name="Permission"/> on <paramref name="ObjectType"/>:<paramref name="ObjectId"/>
/// structurally. When <paramref name="Conditioned"/> is true, a request-time condition was reached
/// on the grant path and must be re-evaluated before the grant is honoured (spec §7.3).
/// The (store, tenant, schema_version) scope is supplied alongside the row, not stored on it.
/// </summary>
public sealed record ReverseIndexRow(
    string Subject,
    string Permission,
    string ObjectType,
    string ObjectId,
    bool Conditioned);
```

Replace the empty `IIndexStore { }` in `src/Relkit.Abstractions/Storage.cs` with:

```csharp
// in src/Relkit.Abstractions/Storage.cs — replaces `public interface IIndexStore { }`
public interface IIndexStore
{
    /// <summary>Insert-or-update each row on its natural key, setting <c>conditioned</c>.</summary>
    Task UpsertAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>Remove every index row for one object (the unit incremental maintenance recomputes).</summary>
    Task DeleteForObjectAsync(TenantContext t, string schemaVersion, string objectType, string objectId,
        IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>Remove specific rows identified by their natural key (targeted incremental diffs).</summary>
    Task DeleteRowsAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>
    /// The ListObjects scan: rows for one (subject, permission, objectType), ordinal-sorted by
    /// object_id, starting strictly after <paramref name="afterObjectId"/> (null = from the start),
    /// capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ReverseIndexRow>> QueryObjectsAsync(TenantContext t, string schemaVersion,
        string subject, string permission, string objectType, int limit, string? afterObjectId,
        CancellationToken ct = default);

    /// <summary>Every index row for one object (the "current rows" side of an incremental diff).</summary>
    Task<IReadOnlyList<ReverseIndexRow>> ReadForObjectAsync(TenantContext t, string schemaVersion,
        string objectType, string objectId, CancellationToken ct = default);

    /// <summary>Drop all index rows for a (store, tenant) across every schema_version (rebuild / schema-change invalidation).</summary>
    Task ClearAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default);

    /// <summary>Has the index been built and marked current for this (store, tenant, schema_version)?</summary>
    Task<bool> IsBuiltAsync(TenantContext t, string schemaVersion, CancellationToken ct = default);

    /// <summary>Record that the index is current for this (store, tenant, schema_version).</summary>
    Task MarkBuiltAsync(TenantContext t, string schemaVersion, IUnitOfWork uow, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter IndexStoreContractTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Abstractions/ReverseIndexRow.cs src/Relkit.Abstractions/Storage.cs tests/Relkit.Abstractions.Tests/IndexStoreContractTests.cs
git commit -m "feat: define IIndexStore members and ReverseIndexRow contract"
```

---

### Task 3: The `index_build_markers` table (migration 003)

**Files:**
- Create: `src/Relkit.Storage.Postgres/Migrations/003_index_build_markers.sql`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/IndexBuildMarkerSchemaTests.cs`

**Interfaces:**
- Produces: `index_build_markers(store_id, tenant_id, schema_version, built_at, PK(store_id, tenant_id, schema_version))`, the table backing `IsBuiltAsync`/`MarkBuiltAsync`. A marker row's presence means "the index is current for this (store, tenant, schema_version)." This is the `RebuildMarker` (spec §7.3): a schema change produces a new `schema_version` with no marker, so the index-backed `ListObjects` (`m2/04`) sees the index is not built for the active version and falls back / triggers a rebuild rather than serving stale rows.

> **CONTRACT GAP (reported).** `index_build_markers` is not a spec §6.3 table — it is M2 maintenance infrastructure for the schema-version staleness check the spec §7.3 requires ("the index is stamped with `schema_version`: a schema change invalidates the index rather than silently serving stale rows"). Flagged in this plan's return alongside `tenant_epochs`/`schema_migrations` (the other sanctioned non-§6.3 tables from `m1/01`).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/IndexBuildMarkerSchemaTests.cs
using Dapper;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexBuildMarkerSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task Migration_creates_the_build_marker_table()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema='public' AND table_name='index_build_markers')");
        exists.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexBuildMarkerSchemaTests`
Expected: FAIL — table does not exist.

- [ ] **Step 3: Write the migration**

```sql
-- src/Relkit.Storage.Postgres/Migrations/003_index_build_markers.sql

-- index_build_markers: presence => the reverse_index is current for (store, tenant, schema_version).
-- M2 maintenance infrastructure for the spec §7.3 schema-version staleness check (CONTRACT GAP).
CREATE TABLE IF NOT EXISTS index_build_markers (
    store_id        text        NOT NULL,
    tenant_id       text        NOT NULL,
    schema_version  text        NOT NULL,
    built_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (store_id, tenant_id, schema_version),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexBuildMarkerSchemaTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Migrations/003_index_build_markers.sql tests/Relkit.Storage.Postgres.Tests/Index/IndexBuildMarkerSchemaTests.cs
git commit -m "feat: add index_build_markers table migration"
```

---

### Task 4: Implement `NpgsqlIndexStore`

**Files:**
- Create: `src/Relkit.Storage.Postgres/NpgsqlIndexStore.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/NpgsqlIndexStoreTests.cs`

**Interfaces:**
- Produces: `NpgsqlIndexStore(string connectionString) : IIndexStore`, every member implemented in Dapper over `reverse_index` and `index_build_markers`. Writes enlist in the supplied `NpgsqlUnitOfWork` (so they commit with the data write); reads open their own short-lived connection. Every statement hard-filters `(store_id, tenant_id)` and, where row-scoped, `schema_version`.
- Consumes: `NpgsqlUnitOfWork.From` (`m1/03`); `ReverseIndexRow`/`IIndexStore`/`TenantContext` (`m0/01`); `Dapper`.

> **Mechanical, no algebra.** This store never decides which rows belong — it stores, deletes, and reads exactly what `m2/02`/`m2/03` hand it. `UpsertAsync` is an `INSERT … ON CONFLICT (natural key) DO UPDATE SET conditioned = EXCLUDED.conditioned`. `QueryObjectsAsync` rides the `m1/01` `ix_reverse_index_scan` index and orders by `object_id` ordinal for stable pagination.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/NpgsqlIndexStoreTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class NpgsqlIndexStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlIndexStore _index = null!;
    private static readonly TenantContext T = new("idxstore", "t");
    private const string V = "v1";

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _index = new NpgsqlIndexStore(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = T.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task UpsertAsync(params ReverseIndexRow[] rows)
    {
        await using var u = await _factory.BeginAsync();
        await _index.UpsertAsync(T, V, rows, u);
        await u.CommitAsync();
    }

    private static ReverseIndexRow Row(string subject, string perm, string ot, string oid, bool cond = false) =>
        new(subject, perm, ot, oid, cond);

    [Fact]
    public async Task Upsert_then_query_returns_rows_sorted_by_object_id()
    {
        await UpsertAsync(
            Row("user:alice", "edit", "species", "wallaby"),
            Row("user:alice", "edit", "species", "kangaroo"),
            Row("user:bob", "edit", "species", "emu"));

        var rows = await _index.QueryObjectsAsync(T, V, "user:alice", "edit", "species", limit: 100, afterObjectId: null);
        rows.Select(r => r.ObjectId).ShouldBe(["kangaroo", "wallaby"]);   // sorted, bob's emu excluded
    }

    [Fact]
    public async Task Query_resumes_strictly_after_the_cursor_and_caps_at_limit()
    {
        await UpsertAsync(
            Row("user:alice", "edit", "species", "a"),
            Row("user:alice", "edit", "species", "b"),
            Row("user:alice", "edit", "species", "c"));

        var page = await _index.QueryObjectsAsync(T, V, "user:alice", "edit", "species", limit: 2, afterObjectId: "a");
        page.Select(r => r.ObjectId).ShouldBe(["b", "c"]);   // strictly after "a", cap 2 not hit
    }

    [Fact]
    public async Task Upsert_updates_conditioned_in_place_on_the_natural_key()
    {
        await UpsertAsync(Row("user:alice", "edit", "species", "kangaroo", cond: false));
        await UpsertAsync(Row("user:alice", "edit", "species", "kangaroo", cond: true));

        var rows = await _index.QueryObjectsAsync(T, V, "user:alice", "edit", "species", 100, null);
        rows.ShouldHaveSingleItem().Conditioned.ShouldBeTrue();   // one row, flipped, not duplicated
    }

    [Fact]
    public async Task DeleteForObject_removes_every_row_for_that_object()
    {
        await UpsertAsync(
            Row("user:alice", "edit", "species", "kangaroo"),
            Row("user:bob", "edit", "species", "kangaroo"),
            Row("user:alice", "edit", "species", "wallaby"));

        await using (var u = await _factory.BeginAsync())
        {
            await _index.DeleteForObjectAsync(T, V, "species", "kangaroo", u);
            await u.CommitAsync();
        }

        (await _index.ReadForObjectAsync(T, V, "species", "kangaroo")).ShouldBeEmpty();
        (await _index.QueryObjectsAsync(T, V, "user:alice", "edit", "species", 100, null))
            .Select(r => r.ObjectId).ShouldBe(["wallaby"]);   // other object untouched
    }

    [Fact]
    public async Task DeleteRows_removes_only_the_named_rows()
    {
        await UpsertAsync(
            Row("user:alice", "edit", "species", "kangaroo"),
            Row("user:alice", "view", "species", "kangaroo"));

        await using (var u = await _factory.BeginAsync())
        {
            await _index.DeleteRowsAsync(T, V, [Row("user:alice", "edit", "species", "kangaroo")], u);
            await u.CommitAsync();
        }

        var rows = await _index.ReadForObjectAsync(T, V, "species", "kangaroo");
        rows.ShouldHaveSingleItem().Permission.ShouldBe("view");
    }

    [Fact]
    public async Task Build_marker_round_trips_per_schema_version()
    {
        (await _index.IsBuiltAsync(T, V)).ShouldBeFalse();

        await using (var u = await _factory.BeginAsync())
        {
            await _index.MarkBuiltAsync(T, V, u);
            await u.CommitAsync();
        }

        (await _index.IsBuiltAsync(T, V)).ShouldBeTrue();
        (await _index.IsBuiltAsync(T, "v2")).ShouldBeFalse();   // a different version is not built
    }

    [Fact]
    public async Task Clear_drops_all_rows_across_versions()
    {
        await UpsertAsync(Row("user:alice", "edit", "species", "kangaroo"));
        await using (var u = await _factory.BeginAsync())
        {
            await _index.UpsertAsync(T, "v2", [Row("user:alice", "edit", "species", "wallaby")], u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await _index.ClearAsync(T, u);
            await u.CommitAsync();
        }

        (await _index.QueryObjectsAsync(T, V, "user:alice", "edit", "species", 100, null)).ShouldBeEmpty();
        (await _index.QueryObjectsAsync(T, "v2", "user:alice", "edit", "species", 100, null)).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter NpgsqlIndexStoreTests`
Expected: FAIL — `NpgsqlIndexStore` does not exist.

- [ ] **Step 3: Implement the store**

```csharp
// src/Relkit.Storage.Postgres/NpgsqlIndexStore.cs
using Dapper;
using Npgsql;
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres;

/// <summary>
/// Dapper-backed <see cref="IIndexStore"/> over the reverse_index and index_build_markers tables.
/// Mechanical CRUD only — the expansion deciding which rows exist lives in m2/02 (rebuild) and
/// m2/03 (incremental). Writes enlist in the supplied unit of work; reads open their own connection.
/// Every statement hard-filters (store_id, tenant_id) and, where row-scoped, schema_version.
/// </summary>
public sealed class NpgsqlIndexStore(string connectionString) : IIndexStore
{
    private readonly string _connectionString = connectionString;

    private const string UpsertSql = """
        INSERT INTO reverse_index
            (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
        VALUES (@store, @tenant, @sv, @subject, @permission, @ot, @oid, @conditioned)
        ON CONFLICT (store_id, tenant_id, schema_version, subject, permission, object_type, object_id)
        DO UPDATE SET conditioned = EXCLUDED.conditioned
        """;

    public async Task UpsertAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var w = NpgsqlUnitOfWork.From(uow);
        foreach (var r in rows)
        {
            await w.Connection.ExecuteAsync(new CommandDefinition(UpsertSql, new
            {
                store = t.Store, tenant = t.Tenant, sv = schemaVersion,
                subject = r.Subject, permission = r.Permission, ot = r.ObjectType, oid = r.ObjectId,
                conditioned = r.Conditioned
            }, transaction: w.Transaction, cancellationToken: ct));
        }
    }

    public async Task DeleteForObjectAsync(TenantContext t, string schemaVersion, string objectType, string objectId,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND object_type = @ot AND object_id = @oid
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion, ot = objectType, oid = objectId },
            transaction: w.Transaction, cancellationToken: ct));
    }

    public async Task DeleteRowsAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var w = NpgsqlUnitOfWork.From(uow);
        const string sql = """
            DELETE FROM reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND subject = @subject AND permission = @permission
              AND object_type = @ot AND object_id = @oid
            """;
        foreach (var r in rows)
        {
            await w.Connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                store = t.Store, tenant = t.Tenant, sv = schemaVersion,
                subject = r.Subject, permission = r.Permission, ot = r.ObjectType, oid = r.ObjectId
            }, transaction: w.Transaction, cancellationToken: ct));
        }
    }

    public async Task<IReadOnlyList<ReverseIndexRow>> QueryObjectsAsync(TenantContext t, string schemaVersion,
        string subject, string permission, string objectType, int limit, string? afterObjectId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT subject AS Subject, permission AS Permission, object_type AS ObjectType,
                   object_id AS ObjectId, conditioned AS Conditioned
            FROM reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND subject = @subject AND permission = @permission AND object_type = @ot
              AND (@after IS NULL OR object_id > @after)
            ORDER BY object_id
            LIMIT @limit
            """, new
            {
                store = t.Store, tenant = t.Tenant, sv = schemaVersion,
                subject, permission, ot = objectType, after = afterObjectId, limit
            }, cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<ReverseIndexRow>> ReadForObjectAsync(TenantContext t, string schemaVersion,
        string objectType, string objectId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT subject AS Subject, permission AS Permission, object_type AS ObjectType,
                   object_id AS ObjectId, conditioned AS Conditioned
            FROM reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND object_type = @ot AND object_id = @oid
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion, ot = objectType, oid = objectId },
            cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task ClearAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM reverse_index WHERE store_id = @store AND tenant_id = @tenant",
            new { store = t.Store, tenant = t.Tenant }, transaction: w.Transaction, cancellationToken: ct));
        await w.Connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM index_build_markers WHERE store_id = @store AND tenant_id = @tenant",
            new { store = t.Store, tenant = t.Tenant }, transaction: w.Transaction, cancellationToken: ct));
    }

    public async Task<bool> IsBuiltAsync(TenantContext t, string schemaVersion, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM index_build_markers
                WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv)
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion }, cancellationToken: ct));
    }

    public async Task MarkBuiltAsync(TenantContext t, string schemaVersion, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO index_build_markers (store_id, tenant_id, schema_version)
            VALUES (@store, @tenant, @sv)
            ON CONFLICT (store_id, tenant_id, schema_version) DO UPDATE SET built_at = now()
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion },
            transaction: w.Transaction, cancellationToken: ct));
    }

    private static ReverseIndexRow Map(Row r) => new(r.Subject, r.Permission, r.ObjectType, r.ObjectId, r.Conditioned);

    private sealed record Row(string Subject, string Permission, string ObjectType, string ObjectId, bool Conditioned);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter NpgsqlIndexStoreTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/NpgsqlIndexStore.cs tests/Relkit.Storage.Postgres.Tests/Index/NpgsqlIndexStoreTests.cs
git commit -m "feat: implement NpgsqlIndexStore over reverse_index"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] `IIndexStore` is no longer empty: it declares `UpsertAsync`, `DeleteForObjectAsync`, `DeleteRowsAsync`, `QueryObjectsAsync`, `ReadForObjectAsync`, `ClearAsync`, `IsBuiltAsync`, `MarkBuiltAsync` (reported as a contract addition).
- [ ] `ReverseIndexRow` exists in `Relkit.Abstractions` with `(Subject, Permission, ObjectType, ObjectId, Conditioned)`.
- [ ] `reverse_index` has `ux_reverse_index_natural` (natural key, conditioned excluded) and keeps `ix_reverse_index_scan` from `m1/01`.
- [ ] `index_build_markers` exists; presence marks the index current for a (store, tenant, schema_version).
- [ ] `NpgsqlIndexStore` upserts in-place on the natural key, scans ordinal-sorted with a cursor, and hard-filters (store, tenant[, schema_version]) on every statement.

## Contract gaps / additions (reported, not changed)

- **`IIndexStore` members added (contract addition).** The foundation contract ships `IIndexStore` empty (`m0/01` Task 6 / README canonical contract). This plan populates it with the eight members above and adds the `ReverseIndexRow` record. The `../README.md` storage-interfaces block should gain these signatures; reported here for the maintainer to propagate, **not** edited by this plan.
- **`index_build_markers` table (non-§6.3).** Maintenance infrastructure for the spec §7.3 schema-version staleness check, mirroring the `m1/01` precedent of `tenant_epochs`/`schema_migrations` as sanctioned non-§6.3 tables.
- **`reverse_index` natural-key unique index.** `m1/01`'s DDL created the table and scan index but no unique constraint; M2 owns the upsert semantics, so `ux_reverse_index_natural` is added here (migration `002`).
