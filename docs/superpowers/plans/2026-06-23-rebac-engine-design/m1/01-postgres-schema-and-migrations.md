# M1/01 — Postgres Schema & Migrations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `Custodex.Storage.Postgres` project (Dapper + Npgsql) and the full Postgres schema for spec §6.3 — `stores`, `schema_versions`, `tenants`, `relation_tuples`, `object_attributes`, `reverse_index` (stamped with `schema_version`), `cache_entries` (`UNLOGGED`), `change_log`, plus the two infrastructure tables the engine requires (`tenant_epochs` for the cache epoch counter, `schema_migrations` for the migration tracker) — delivered through an idempotent, ordered, embedded-SQL migration runner verified against a real Postgres via Testcontainers.

**Architecture:** Migrations are plain `.sql` scripts embedded as assembly resources and applied in lexical order by a small `MigrationRunner`. The runner records each applied script in a `schema_migrations` tracking table and skips already-applied scripts, so running it repeatedly is a no-op. No EF Core; no migration framework. Every engine table carries `store_id` and `tenant_id` (where tenant-scoped) so storage operations can hard-filter on both (spec §6.3). The forward index on `relation_tuples` serves Check; the reverse index serves traversal and ListSubjects.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 license metadata; all I/O methods `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. Depends on `m0/01` (the `Custodex.Abstractions` contract). **No EF Core.**

## Shared decisions (locked — used verbatim by m1/03, m1/04, m1/07)

These four M1 plans share a database and types. Pin them once, here, and reuse them unchanged:

- **Tenant-scoped tables** carry `store_id text` and `tenant_id text` and are FK'd to `stores`/`tenants`. **Every** query filters on both.
- **`relation_tuples` natural key** (the `UNIQUE` for `ON CONFLICT` upsert in m1/04) is
  `(store_id, tenant_id, object_type, object_id, relation, subject_type, subject_id, subject_relation)`.
  `subject_relation` is nullable; the unique index uses `COALESCE(subject_relation, '')` so two tuples differing only by a null vs `''` relation never collide.
- **Epoch counter** lives in `tenant_epochs(store_id, tenant_id, epoch bigint, PK(store_id, tenant_id))` — the authoritative per-(store,tenant) counter behind `ICacheStore.GetEpochAsync`/`BumpEpochAsync`. This table is **not** in spec §6.3 (see Contract gaps in this plan's return). `cache_entries.epoch` is a *stamp copied onto each cache row*, a different thing.
- **Migration tracker** is `schema_migrations(version text primary key, applied_at timestamptz default now())` — runner infrastructure, also not a §6.3 table.
- **jsonb columns**: `condition_params`, `object_attributes.attributes`, `schema_versions.definition`, and `change_log.before`/`after` are `jsonb`.

---

### Task 1: Create the `Custodex.Storage.Postgres` project and test project

**Files:**
- Create: `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj`
- Create: `tests/Custodex.Storage.Postgres.Tests/Custodex.Storage.Postgres.Tests.csproj`
- Test: `tests/Custodex.Storage.Postgres.Tests/WiringTests.cs`

**Interfaces:**
- Produces: the `Custodex.Storage.Postgres` assembly referencing `Custodex.Abstractions`, with Npgsql + Dapper; a test project with Testcontainers.

- [ ] **Step 1: Create projects and references**

Run:
```bash
dotnet new classlib -n Custodex.Storage.Postgres -o src/Custodex.Storage.Postgres -f net10.0
dotnet new xunit -n Custodex.Storage.Postgres.Tests -o tests/Custodex.Storage.Postgres.Tests -f net10.0
rm src/Custodex.Storage.Postgres/Class1.cs tests/Custodex.Storage.Postgres.Tests/UnitTest1.cs
dotnet sln add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
dotnet add src/Custodex.Storage.Postgres reference src/Custodex.Abstractions
dotnet add src/Custodex.Storage.Postgres package Npgsql
dotnet add src/Custodex.Storage.Postgres package Dapper
dotnet add tests/Custodex.Storage.Postgres.Tests reference src/Custodex.Storage.Postgres
dotnet add tests/Custodex.Storage.Postgres.Tests reference src/Custodex.Abstractions
dotnet add tests/Custodex.Storage.Postgres.Tests package Shouldly
dotnet add tests/Custodex.Storage.Postgres.Tests package Testcontainers.PostgreSql
dotnet add tests/Custodex.Storage.Postgres.Tests package Npgsql
dotnet add tests/Custodex.Storage.Postgres.Tests package Dapper
```

- [ ] **Step 2: Mark the SQL scripts as embedded resources**

Add to `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj` inside the `<Project>`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Migrations/*.sql" />
  </ItemGroup>
```

- [ ] **Step 3: Write the wiring test**

```csharp
// tests/Custodex.Storage.Postgres.Tests/WiringTests.cs
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

public class WiringTests
{
    [Fact]
    public void Storage_assembly_is_referenced()
    {
        typeof(Custodex.Storage.Postgres.MigrationRunner).Assembly.GetName().Name
            .ShouldBe("Custodex.Storage.Postgres");
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter WiringTests`
Expected: FAIL — `MigrationRunner` does not exist yet.

- [ ] **Step 5: Add a placeholder `MigrationRunner`** (filled in Task 3)

```csharp
// src/Custodex.Storage.Postgres/MigrationRunner.cs
namespace Custodex.Storage.Postgres;

public sealed partial class MigrationRunner { }
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter WiringTests`
Expected: PASS (1 test).

- [ ] **Step 7: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "chore: scaffold Custodex.Storage.Postgres project"
```

---

### Task 2: The schema migration script (all §6.3 tables + infrastructure tables)

**Files:**
- Create: `src/Custodex.Storage.Postgres/Migrations/001_initial_schema.sql`

**Interfaces:**
- Produces: every table and index in spec §6.3, plus `tenant_epochs` and `schema_migrations`. This script is the single source of DDL; Task 3 applies it.

- [ ] **Step 1: Write the initial schema script**

> The whole script is wrapped so every statement is idempotent (`IF NOT EXISTS`), letting the runner re-apply safely and letting a partial run be re-run. `relation_tuples` is indexed forward (Check) and reverse (traversal / ListSubjects) exactly as spec §6.3 requires.

```sql
-- src/Custodex.Storage.Postgres/Migrations/001_initial_schema.sql

-- ── stores: one per consuming application (spec §6.1) ───────────────────────
CREATE TABLE IF NOT EXISTS stores (
    id    text PRIMARY KEY,
    name  text NOT NULL DEFAULT ''
);

-- ── schema_versions: the versioned application schema lineage (spec §6.3) ────
CREATE TABLE IF NOT EXISTS schema_versions (
    store_id    text   NOT NULL REFERENCES stores(id),
    version     text   NOT NULL,
    definition  jsonb  NOT NULL,
    is_active   boolean NOT NULL DEFAULT false,
    PRIMARY KEY (store_id, version)
);

-- At most one active schema per store.
CREATE UNIQUE INDEX IF NOT EXISTS ux_schema_versions_active
    ON schema_versions (store_id)
    WHERE is_active;

-- ── tenants: the data-isolation discriminator within a store (spec §6.1) ─────
CREATE TABLE IF NOT EXISTS tenants (
    store_id   text NOT NULL REFERENCES stores(id),
    tenant_id  text NOT NULL,
    PRIMARY KEY (store_id, tenant_id)
);

-- ── relation_tuples: the atomic stored facts (spec §6.2/§6.3) ────────────────
CREATE TABLE IF NOT EXISTS relation_tuples (
    store_id           text  NOT NULL,
    tenant_id          text  NOT NULL,
    object_type        text  NOT NULL,
    object_id          text  NOT NULL,
    relation           text  NOT NULL,
    subject_type       text  NOT NULL,
    subject_id         text  NOT NULL,
    subject_relation   text  NULL,
    condition_name     text  NULL,
    condition_params   jsonb NULL,
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- Natural key (locked in shared decisions): COALESCE collapses null vs '' relation.
CREATE UNIQUE INDEX IF NOT EXISTS ux_relation_tuples_natural
    ON relation_tuples (
        store_id, tenant_id, object_type, object_id, relation,
        subject_type, subject_id, COALESCE(subject_relation, '')
    );

-- Forward index for Check: (store, tenant, object_type, object_id, relation).
CREATE INDEX IF NOT EXISTS ix_relation_tuples_forward
    ON relation_tuples (store_id, tenant_id, object_type, object_id, relation);

-- Reverse index for traversal / ListSubjects: (store, tenant, subject_type, subject_id).
CREATE INDEX IF NOT EXISTS ix_relation_tuples_reverse
    ON relation_tuples (store_id, tenant_id, subject_type, subject_id);

-- ── object_attributes: synced authz-relevant resource fields (spec §6.4) ─────
CREATE TABLE IF NOT EXISTS object_attributes (
    store_id     text  NOT NULL,
    tenant_id    text  NOT NULL,
    object_type  text  NOT NULL,
    object_id    text  NOT NULL,
    attributes   jsonb NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (store_id, tenant_id, object_type, object_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- ── reverse_index: the maintained structural expansion (M2; spec §6.3/§7.3) ──
CREATE TABLE IF NOT EXISTS reverse_index (
    store_id        text    NOT NULL,
    tenant_id       text    NOT NULL,
    schema_version  text    NOT NULL,
    subject         text    NOT NULL,
    permission      text    NOT NULL,
    object_type     text    NOT NULL,
    object_id       text    NOT NULL,
    conditioned     boolean NOT NULL DEFAULT false,
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- Scan index for ListObjects: (store, tenant, schema_version, subject, permission, object_type).
CREATE INDEX IF NOT EXISTS ix_reverse_index_scan
    ON reverse_index (store_id, tenant_id, schema_version, subject, permission, object_type);

-- ── cache_entries: UNLOGGED Postgres-native cache (M2; spec §9.1) ────────────
CREATE UNLOGGED TABLE IF NOT EXISTS cache_entries (
    store_id    text        NOT NULL,
    tenant_id   text        NOT NULL,
    key         text        NOT NULL,
    value       bytea       NOT NULL,
    epoch       bigint      NOT NULL,
    expires_at  timestamptz NOT NULL,
    PRIMARY KEY (store_id, tenant_id, key)
);

CREATE INDEX IF NOT EXISTS ix_cache_entries_expiry
    ON cache_entries (expires_at);

-- ── change_log: append-only config-change audit (spec §6.5) ──────────────────
CREATE TABLE IF NOT EXISTS change_log (
    id           bigserial   PRIMARY KEY,
    store_id     text        NOT NULL,
    tenant_id    text        NOT NULL,
    actor        text        NOT NULL,
    operation    text        NOT NULL,
    target       text        NOT NULL,
    before       jsonb       NULL,
    after        jsonb       NULL,
    occurred_at  timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE INDEX IF NOT EXISTS ix_change_log_read
    ON change_log (store_id, tenant_id, occurred_at DESC);

-- ── tenant_epochs: authoritative cache-epoch counter (CONTRACT GAP) ──────────
-- Backs ICacheStore.GetEpochAsync / BumpEpochAsync; not a spec §6.3 table.
CREATE TABLE IF NOT EXISTS tenant_epochs (
    store_id   text   NOT NULL,
    tenant_id  text   NOT NULL,
    epoch      bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (store_id, tenant_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);
```

> The `schema_migrations` tracking table is created by the runner itself (Task 3), not by a migration script, so the runner can bootstrap on an empty database.

- [ ] **Step 2: Commit**

```bash
git add src/Custodex.Storage.Postgres/Migrations/001_initial_schema.sql
git commit -m "feat: add initial Postgres schema for all engine tables"
```

---

### Task 3: The idempotent migration runner

**Files:**
- Modify: `src/Custodex.Storage.Postgres/MigrationRunner.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/PostgresFixture.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/MigrationRunnerTests.cs`

**Interfaces:**
- Produces: `MigrationRunner` with `static Task ApplyAsync(NpgsqlConnection connection, CancellationToken ct = default)` that creates `schema_migrations` if absent, loads embedded `Migrations/*.sql` resources in name order, applies any not yet recorded (each script + its tracking insert in one transaction), and records them. Re-running applies nothing.

- [ ] **Step 1: Write the Testcontainers fixture** (shared by every integration test in this project)

```csharp
// tests/Custodex.Storage.Postgres.Tests/PostgresFixture.cs
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Custodex.Storage.Postgres.Tests/MigrationRunnerTests.cs
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class MigrationRunnerTests(PostgresFixture fx)
{
    private static readonly string[] ExpectedTables =
    [
        "stores", "schema_versions", "tenants", "relation_tuples",
        "object_attributes", "reverse_index", "cache_entries",
        "change_log", "tenant_epochs", "schema_migrations"
    ];

    [Fact]
    public async Task Apply_creates_every_table()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"))
            .ToHashSet();

        foreach (var t in ExpectedTables)
            tables.ShouldContain(t);
    }

    [Fact]
    public async Task Apply_creates_forward_and_reverse_indexes()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var indexes = (await conn.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'"))
            .ToHashSet();

        indexes.ShouldContain("ix_relation_tuples_forward");
        indexes.ShouldContain("ix_relation_tuples_reverse");
        indexes.ShouldContain("ux_relation_tuples_natural");
        indexes.ShouldContain("ix_reverse_index_scan");
    }

    [Fact]
    public async Task Cache_entries_is_unlogged()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        // relpersistence 'u' = unlogged. Cast to text so Npgsql maps the internal "char" cleanly.
        var persistence = await conn.ExecuteScalarAsync<string>(
            "SELECT relpersistence::text FROM pg_class WHERE relname = 'cache_entries'");
        persistence.ShouldBe("u");
    }

    [Fact]
    public async Task Apply_is_idempotent_and_records_each_script_once()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await MigrationRunner.ApplyAsync(conn);   // second run must be a no-op

        var applied = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM schema_migrations WHERE version = '001_initial_schema'");
        applied.ShouldBe(1);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter MigrationRunnerTests`
Expected: FAIL — `MigrationRunner.ApplyAsync` does not exist.

- [ ] **Step 4: Implement the runner**

```csharp
// src/Custodex.Storage.Postgres/MigrationRunner.cs
using System.Reflection;
using Npgsql;

namespace Custodex.Storage.Postgres;

public sealed partial class MigrationRunner
{
    private const string ResourcePrefix = "Custodex.Storage.Postgres.Migrations.";

    public static async Task ApplyAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        await EnsureTrackingTableAsync(connection, ct);

        var applied = await LoadAppliedVersionsAsync(connection, ct);

        foreach (var (version, sql) in LoadScripts())
        {
            if (applied.Contains(version))
                continue;

            await using var tx = await connection.BeginTransactionAsync(ct);

            await using (var scriptCmd = new NpgsqlCommand(sql, connection, tx))
                await scriptCmd.ExecuteNonQueryAsync(ct);

            await using (var trackCmd = new NpgsqlCommand(
                "INSERT INTO schema_migrations (version) VALUES (@v)", connection, tx))
            {
                trackCmd.Parameters.AddWithValue("v", version);
                await trackCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }

    private static async Task EnsureTrackingTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string ddl =
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())";
        await using var cmd = new NpgsqlCommand(ddl, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> LoadAppliedVersionsAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT version FROM schema_migrations", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    // Embedded "Migrations/NNN_name.sql" → ("NNN_name", scriptText), ordered by version.
    private static IEnumerable<(string Version, string Sql)> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Version: VersionFromResourceName(n), ResourceName: n))
            .OrderBy(x => x.Version, StringComparer.Ordinal)
            .Select(x => (x.Version, Sql: ReadResource(assembly, x.ResourceName)))
            .ToList();
    }

    private static string VersionFromResourceName(string resourceName)
    {
        var tail = resourceName[ResourcePrefix.Length..];          // "001_initial_schema.sql"
        return tail[..^".sql".Length];                             // "001_initial_schema"
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded migration: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter MigrationRunnerTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add idempotent embedded-SQL migration runner"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Every spec §6.3 table exists: `stores`, `schema_versions`, `tenants`, `relation_tuples`, `object_attributes`, `reverse_index` (with `schema_version`), `cache_entries` (`UNLOGGED`), `change_log`.
- [ ] `relation_tuples` has the forward index, the reverse index, and the natural-key unique index.
- [ ] `cache_entries` is `UNLOGGED` (`pg_class.relpersistence = 'u'`).
- [ ] `tenant_epochs` and `schema_migrations` exist; both are flagged in the plan return as non-§6.3 (one a contract gap, one sanctioned runner infrastructure).
- [ ] `MigrationRunner.ApplyAsync` is idempotent: a second run inserts no new `schema_migrations` rows.
