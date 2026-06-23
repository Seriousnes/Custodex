# M1/09 — DI & Usable Library Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Relkit a usable library: concrete `IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager` implementations that (a) supply the `actor` and compute before/after diffs, calling `m1/07`'s `AuditedWritePath` so the data write, `change_log` entry, and cache-epoch bump all commit in **one** unit of work (spec §6.5 / §9.2); the `services.AddRelkit().UsePostgres(connString).UseSchema(builder)` DI surface (spec §10.1); `AddRelkitInstrumentation()` wiring the `"Relkit"` `ActivitySource` + `Meter` into OpenTelemetry (spec §11.4); and an end-to-end Testcontainers sample (define schema → write tuples → Check/ListObjects) proving the wired-up engine works against real Postgres.

**Architecture:** The managers own **actor + diff orchestration**: each write carries an `actor` (supplied by the consuming application — the engine never invents identity, spec §6.5), the manager assembles before/after images, opens one `IUnitOfWork`, and delegates the in-transaction sequencing to `AuditedWritePath` (`m1/07`), then commits. Because `AuditedWritePath` already lives in `Relkit.Storage.Postgres` and depends only on `Relkit.Abstractions` interfaces, the concrete managers live in `Relkit.Storage.Postgres` too (they need `NpgsqlUnitOfWorkFactory` to begin the transaction and `AuditedWritePath` to sequence the writes). `AddRelkit()` returns a builder; `.UsePostgres(conn)` registers the Postgres stores, `NpgsqlCteAuthorizer` as the primary `IAuthorizer` (wrapped by `m0/08`'s `CachingAuthorizer` for the cross-request cache), the managers, and `PostgresCacheStore`; `.UseSchema(builder)` registers a startup schema to validate + activate. `AddRelkitInstrumentation()` is an OpenTelemetry extension that enables the `"Relkit"` activity source and meter (`RelkitDiagnostics`, `m0/01`).

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, `Microsoft.Extensions.DependencyInjection.Abstractions`, `OpenTelemetry` (extension registration only), xUnit, Shouldly, `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. Depends on `m0/01` (contracts + `RelkitDiagnostics`), `m0/02`/`m0/03` (`SchemaBuilder`, `SchemaValidator`), `m0/08` (`CachingAuthorizer`), `m1/01`–`m1/05`/`m1/07` (Postgres schema, UoW, stores, `NpgsqlCteAuthorizer`, `AuditedWritePath`, `PostgresCacheStore`). **No EF Core.**

> **Aspire alignment** (see `../README.md` → Aspire integration): implement `AddRelkitInstrumentation()` as OpenTelemetry builder extensions — `tracing.AddSource("Relkit")` and `metrics.AddMeter("Relkit")` — that `Relkit.Service` calls *after* `AddServiceDefaults()`. It registers the library's source/meter into the existing ServiceDefaults OTel pipeline; it must not stand up its own.

## Shared decisions (locked)

- **Managers live in `Relkit.Storage.Postgres`** (they orchestrate `AuditedWritePath` + `NpgsqlUnitOfWorkFactory`, both provider-side). They implement the `Relkit.Abstractions` manager interfaces, so a consumer depends only on the interface.
- **Actor is caller-supplied** on every write (`IRelationManager.WriteTuplesAsync(tenant, actor, …)`); the manager never invents it.
- **Before/after diffs:** `WriteTuplesAsync` → adds audit as `after: tuple` (op `write`), deletes as `before: tuple` (op `delete`); `WriteAttributesAsync` → reads current attributes as `before`, new as `after`; `SetActiveSchema` → `after: version`.
- **One unit of work per manager call:** the manager begins an owned `IUnitOfWork` (or, for atomic co-commit with the consuming app's domain write, an `Enlist`-ed supplied one — exposed via an overload), passes it to `AuditedWritePath`, and commits.

---

### Task 1: Create `Relkit.Extensions.DependencyInjection` wiring project and `RelkitBuilder`

**Files:**
- Create: `src/Relkit.Extensions.DependencyInjection/Relkit.Extensions.DependencyInjection.csproj`
- Create: `src/Relkit.Extensions.DependencyInjection/RelkitBuilder.cs`
- Create: `src/Relkit.Extensions.DependencyInjection/RelkitServiceCollectionExtensions.cs`
- Create: `tests/Relkit.Extensions.DependencyInjection.Tests/Relkit.Extensions.DependencyInjection.Tests.csproj`
- Test: `tests/Relkit.Extensions.DependencyInjection.Tests/RelkitBuilderTests.cs`

**Interfaces:**
- Produces: `IServiceCollection.AddRelkit() -> RelkitBuilder`; `RelkitBuilder` exposing `IServiceCollection Services { get; }` and a place to hold the chosen schema builder; `RelkitBuilder UseSchema(SchemaBuilder builder)` (stores the schema to activate at startup) and `UseSchema(Schema schema)`. `UsePostgres` is added in Task 4 (it lives in the Postgres package to avoid a DI→Postgres reference cycle; this project references `Relkit.Abstractions` + `Microsoft.Extensions.DependencyInjection.Abstractions` only).
- Consumes: `Schema` (`m0/01`), `SchemaBuilder` (`m0/02`).

> **Why a separate DI project.** `AddRelkit()` must be callable without forcing a Postgres dependency on consumers who use the in-memory provider in tests. The base builder lives in `Relkit.Extensions.DependencyInjection` (Abstractions only); `UsePostgres` is an extension method shipped in `Relkit.Storage.Postgres` (Task 4) that the consumer references when they want Postgres. This matches spec §10.1 ("switching to the remote service is one registration change").

- [ ] **Step 1: Create the projects and references**

Run:
```bash
dotnet new classlib -n Relkit.Extensions.DependencyInjection -o src/Relkit.Extensions.DependencyInjection -f net10.0
dotnet new xunit -n Relkit.Extensions.DependencyInjection.Tests -o tests/Relkit.Extensions.DependencyInjection.Tests -f net10.0
rm src/Relkit.Extensions.DependencyInjection/Class1.cs tests/Relkit.Extensions.DependencyInjection.Tests/UnitTest1.cs
dotnet sln add src/Relkit.Extensions.DependencyInjection tests/Relkit.Extensions.DependencyInjection.Tests
dotnet add src/Relkit.Extensions.DependencyInjection reference src/Relkit.Abstractions
dotnet add src/Relkit.Extensions.DependencyInjection reference src/Relkit.Core
dotnet add src/Relkit.Extensions.DependencyInjection package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add tests/Relkit.Extensions.DependencyInjection.Tests reference src/Relkit.Extensions.DependencyInjection
dotnet add tests/Relkit.Extensions.DependencyInjection.Tests reference src/Relkit.Core
dotnet add tests/Relkit.Extensions.DependencyInjection.Tests package Shouldly
dotnet add tests/Relkit.Extensions.DependencyInjection.Tests package Microsoft.Extensions.DependencyInjection
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Relkit.Extensions.DependencyInjection.Tests/RelkitBuilderTests.cs
using Microsoft.Extensions.DependencyInjection;
using Relkit.Core;
using Relkit.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Relkit.Extensions.DependencyInjection.Tests;

public class RelkitBuilderTests
{
    [Fact]
    public void AddRelkit_returns_a_builder_over_the_same_services()
    {
        var services = new ServiceCollection();
        var builder = services.AddRelkit();
        builder.Services.ShouldBeSameAs(services);
    }

    [Fact]
    public void UseSchema_captures_the_built_schema_for_startup_activation()
    {
        var services = new ServiceCollection();
        var builder = services.AddRelkit()
            .UseSchema(new SchemaBuilder("v1")
                .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))));
        builder.StartupSchema.ShouldNotBeNull();
        builder.StartupSchema!.Version.ShouldBe("v1");
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Relkit.Extensions.DependencyInjection.Tests --filter RelkitBuilderTests`
Expected: FAIL — `AddRelkit` / `RelkitBuilder` do not exist.

- [ ] **Step 4: Implement the builder and entry point**

```csharp
// src/Relkit.Extensions.DependencyInjection/RelkitBuilder.cs
using Microsoft.Extensions.DependencyInjection;
using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Extensions.DependencyInjection;

/// <summary>Fluent registration surface for Relkit (spec §10.1). Provider packages add extension methods (e.g. UsePostgres).</summary>
public sealed class RelkitBuilder
{
    public RelkitBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>The schema to validate and activate at startup, if configured via UseSchema.</summary>
    public Schema? StartupSchema { get; private set; }

    public RelkitBuilder UseSchema(Schema schema)
    {
        StartupSchema = schema;
        return this;
    }

    public RelkitBuilder UseSchema(SchemaBuilder builder) => UseSchema(builder.Build());
}
```

```csharp
// src/Relkit.Extensions.DependencyInjection/RelkitServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;

namespace Relkit.Extensions.DependencyInjection;

public static class RelkitServiceCollectionExtensions
{
    /// <summary>Begin Relkit registration. Chain a provider (e.g. <c>.UsePostgres(conn)</c>) and <c>.UseSchema(builder)</c>.</summary>
    public static RelkitBuilder AddRelkit(this IServiceCollection services) => new(services);
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Extensions.DependencyInjection.Tests --filter RelkitBuilderTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Extensions.DependencyInjection tests/Relkit.Extensions.DependencyInjection.Tests
git commit -m "feat: add AddRelkit builder entry point"
```

---

### Task 2: The concrete managers — actor + diff orchestration over `AuditedWritePath`

**Files:**
- Create: `src/Relkit.Storage.Postgres/Managers/RelkitRelationManager.cs`
- Create: `src/Relkit.Storage.Postgres/Managers/RelkitSchemaManager.cs`
- Create: `src/Relkit.Storage.Postgres/Managers/RelkitStoreTenantManager.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Managers/RelationManagerTests.cs`

**Interfaces:**
- Produces:
  - `RelkitRelationManager : IRelationManager` — `WriteTuplesAsync`/`DeleteTuplesAsync`/`WriteAttributesAsync` begin an owned `IUnitOfWork`, compute before/after, call `AuditedWritePath`, and commit; `ReadTuplesAsync` (via `NpgsqlRelationStore` + a filtered read) and `ReadChangeLogAsync` (via `NpgsqlChangeLogStore`).
  - `RelkitSchemaManager : ISchemaManager` — `ValidateSchema` (delegates to `m0/03` `SchemaValidator`), `SetActiveSchemaAsync` (validates then calls `AuditedWritePath.SetSchemaAsync` in one UoW; throws `SchemaValidationException` on invalid), `GetActiveSchemaAsync`.
  - `RelkitStoreManager : IStoreManager` and `RelkitTenantManager : ITenantManager` — insert the `stores` / `tenants` rows.
- Consumes: `AuditedWritePath`, `NpgsqlUnitOfWorkFactory`, the Npgsql stores (`m1/03`/`m1/04`/`m1/07`); `SchemaValidator` (`m0/03`); the manager interfaces + `SchemaValidationException` (`m0/01`).

> **Diff assembly (the m1/07 hand-off).** `m1/07`'s `AuditedWritePath` is the in-transaction sequencer; it does **not** invent the actor or compute the diff (its plan flags this as `m1/09`'s job). Here the manager supplies them: tuple writes pass `after: tuple`, deletes pass `before: tuple`; attribute writes read the current bag first (`IAttributeStore.GetAsync`) as `before`. The manager owns the `IUnitOfWork` lifetime; `AuditedWritePath` runs the data write + `change_log` + epoch bump on it; the manager commits — so all four effects are atomic (spec §9.2).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Managers/RelationManagerTests.cs
using Relkit.Abstractions;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Managers;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Managers;

[Collection("postgres")]
public class RelationManagerTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private RelkitRelationManager _manager = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlChangeLogStore _changeLog = null!;
    private PostgresCacheStore _cache = null!;
    private readonly TenantContext _t = new("zoo", "mgr");

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _changeLog = new NpgsqlChangeLogStore(fx.ConnectionString);
        _cache = new PostgresCacheStore(fx.ConnectionString, _t);
        var auditedPath = new AuditedWritePath(_relations, attributes, schemas, _changeLog, _cache);
        _manager = new RelkitRelationManager(_factory, _relations, attributes, _changeLog, auditedPath);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RelationTuple Tuple => new(
        new EntityRef("category", "drugs"), "dispenser", new SubjectRef("group", "vets", "member"));

    [Fact]
    public async Task WriteTuples_persists_tuple_audits_actor_and_bumps_epoch_atomically()
    {
        await _manager.WriteTuplesAsync(_t, "dr-admin", [Tuple]);

        (await _relations.GetByObjectAsync(_t, new EntityRef("category", "drugs"), "dispenser"))
            .ShouldHaveSingleItem();

        var log = await _changeLog.ReadAsync(_t, new ChangeLogFilter());
        var e = log.ShouldHaveSingleItem();
        e.Actor.ShouldBe("dr-admin");
        e.Operation.ShouldBe("write");

        (await _cache.GetEpochAsync(_t)).ShouldBe(1);
    }

    [Fact]
    public async Task DeleteTuples_records_a_delete_audit_with_before_image()
    {
        await _manager.WriteTuplesAsync(_t, "dr-admin", [Tuple]);
        await _manager.DeleteTuplesAsync(_t, "dr-admin", [Tuple]);

        (await _relations.GetByObjectAsync(_t, new EntityRef("category", "drugs"), "dispenser")).ShouldBeEmpty();

        var log = await _changeLog.ReadAsync(_t, new ChangeLogFilter());
        log.ShouldContain(e => e.Operation == "delete");
        (await _cache.GetEpochAsync(_t)).ShouldBe(2);   // one bump per manager call
    }

    [Fact]
    public async Task ReadTuples_filters_by_object_type()
    {
        await _manager.WriteTuplesAsync(_t, "admin", [Tuple]);
        var read = await _manager.ReadTuplesAsync(_t, new TupleFilter(ObjectType: "category"));
        read.ShouldContain(x => x.Object.Id == "drugs");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter RelationManagerTests`
Expected: FAIL — the managers do not exist.

- [ ] **Step 3: Implement the managers**

First, the relation store needs a filtered read for `ReadTuplesAsync`. Add it to `NpgsqlRelationStore` (provider convenience, not on the portable `IRelationStore`):

```csharp
// Add to src/Relkit.Storage.Postgres/NpgsqlRelationStore.cs (alongside the existing members)

    /// <summary>Admin/audit read: tuples matching a partial filter, tenant-scoped.</summary>
    public async Task<IReadOnlyList<RelationTuple>> QueryAsync(
        TenantContext t, TupleFilter filter, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND (@ot IS NULL OR object_type = @ot)
              AND (@oid IS NULL OR object_id = @oid)
              AND (@rel IS NULL OR relation = @rel)
              AND (@st IS NULL OR subject_type = @st)
              AND (@sid IS NULL OR subject_id = @sid)
            """,
            new
            {
                store = t.Store, tenant = t.Tenant,
                ot = filter.ObjectType, oid = filter.ObjectId, rel = filter.Relation,
                st = filter.SubjectType, sid = filter.SubjectId,
            },
            cancellationToken: ct));
        return rows.Select(Map).ToList();
    }
```

```csharp
// src/Relkit.Storage.Postgres/Managers/RelkitRelationManager.cs
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Managers;

/// <summary>
/// Concrete <see cref="IRelationManager"/>. Each write supplies the caller's actor and before/after
/// images, then sequences the data write + change_log + epoch bump on ONE unit of work via
/// <see cref="AuditedWritePath"/> (spec §6.5 / §9.2). The engine never invents the actor.
/// </summary>
public sealed class RelkitRelationManager(
    NpgsqlUnitOfWorkFactory uowFactory,
    NpgsqlRelationStore relations,
    IAttributeStore attributes,
    NpgsqlChangeLogStore changeLog,
    AuditedWritePath audited) : IRelationManager
{
    public async Task WriteTuplesAsync(
        TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.WriteTuplesAsync(tenant, actor, tuples, [], uow, ct);
        await uow.CommitAsync(ct);
    }

    public async Task DeleteTuplesAsync(
        TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.WriteTuplesAsync(tenant, actor, [], tuples, uow, ct);
        await uow.CommitAsync(ct);
    }

    public async Task WriteAttributesAsync(
        TenantContext tenant, string actor, EntityRef obj,
        IReadOnlyDictionary<string, object?> attrs, CancellationToken ct = default)
    {
        var before = await attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.WriteAttributesAsync(tenant, actor, obj, before, attrs, uow, ct);
        await uow.CommitAsync(ct);
    }

    public Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(
        TenantContext tenant, TupleFilter filter, CancellationToken ct = default)
        => relations.QueryAsync(tenant, filter, ct);

    public Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(
        TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default)
        => changeLog.ReadAsync(tenant, filter, ct);
}
```

```csharp
// src/Relkit.Storage.Postgres/Managers/RelkitSchemaManager.cs
using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Storage.Postgres.Managers;

/// <summary>
/// Concrete <see cref="ISchemaManager"/>. Validates via the m0/03 SchemaValidator, then activates
/// the schema and audits the change in one unit of work. Schema is per-store; the audit tenant is
/// the store's default tenant carried in the call. An invalid schema throws SchemaValidationException.
/// </summary>
public sealed class RelkitSchemaManager(
    NpgsqlUnitOfWorkFactory uowFactory,
    NpgsqlSchemaStore schemas,
    AuditedWritePath audited) : ISchemaManager
{
    public SchemaValidationResult ValidateSchema(Schema schema) => SchemaValidator.Validate(schema);

    public async Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
    {
        var validation = SchemaValidator.Validate(schema);
        if (!validation.IsValid) throw new SchemaValidationException(validation.Errors);

        // Schema is per-store; audit it against the store's primary tenant context.
        var tenant = new TenantContext(store, store);
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.SetSchemaAsync(store, tenant, actor: "schema-author", schema, uow, ct);
        await uow.CommitAsync(ct);
    }

    public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default)
        => schemas.GetActiveAsync(store, ct);
}
```

> **Schema-audit tenant note.** A schema is per-store (not per-tenant), but `change_log` is tenant-scoped (FK to `tenants`). The manager audits the schema change against a per-store bookkeeping tenant `(store, store)` so the FK is satisfied; the consuming app may seed that tenant via `ITenantManager`. The audit actor for schema changes defaults to `"schema-author"`; an overload taking an explicit actor is added if a consumer needs it.

```csharp
// src/Relkit.Storage.Postgres/Managers/RelkitStoreTenantManager.cs
using Dapper;
using Npgsql;
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Managers;

public sealed class RelkitStoreManager(string connectionString) : IStoreManager
{
    public async Task CreateStoreAsync(string store, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO stores (id) VALUES (@store) ON CONFLICT DO NOTHING",
            new { store }, cancellationToken: ct));
    }
}

public sealed class RelkitTenantManager(string connectionString) : ITenantManager
{
    public async Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tenants (store_id, tenant_id) VALUES (@store, @tenant) ON CONFLICT DO NOTHING
            """,
            new { store = tenant.Store, tenant = tenant.Tenant }, cancellationToken: ct));
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter RelationManagerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres tests/Relkit.Storage.Postgres.Tests
git commit -m "feat: add concrete managers with actor + diff orchestration over AuditedWritePath"
```

---

### Task 3: Schema-manager validation + atomic activation tests

**Files:**
- Test: `tests/Relkit.Storage.Postgres.Tests/Managers/SchemaManagerTests.cs`

**Interfaces:**
- Consumes: `RelkitSchemaManager`. Proves an invalid schema throws `SchemaValidationException` (no DB write), and a valid schema activates + audits atomically.

- [ ] **Step 1: Write the tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Managers/SchemaManagerTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Managers;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Managers;

[Collection("postgres")]
public class SchemaManagerTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private RelkitSchemaManager _manager = null!;
    private NpgsqlSchemaStore _schemas = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        var relations = new NpgsqlRelationStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var changeLog = new NpgsqlChangeLogStore(fx.ConnectionString);
        var cache = new PostgresCacheStore(fx.ConnectionString, new TenantContext("sm", "sm"));
        var audited = new AuditedWritePath(relations, attributes, _schemas, changeLog, cache);
        _manager = new RelkitSchemaManager(_factory, _schemas, audited);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task SeedStoreAndTenantAsync(NpgsqlUnitOfWorkFactory factory, string store)
    {
        await using var u = await factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @s) ON CONFLICT DO NOTHING",
            new { s = store }, uow.Transaction);   // per-store bookkeeping tenant (store, store)
        await u.CommitAsync();
    }

    [Fact]
    public async Task Valid_schema_activates_and_is_readable()
    {
        const string store = "sm-valid";
        await SeedStoreAndTenantAsync(_factory, store);
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();

        await _manager.SetActiveSchemaAsync(store, schema);
        (await _manager.GetActiveSchemaAsync(store))!.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task Invalid_schema_throws_and_writes_nothing()
    {
        const string store = "sm-invalid";
        await SeedStoreAndTenantAsync(_factory, store);
        // A permission referencing a relation that does not exist => m0/03 validation fails.
        var bad = new Schema("v1",
            [new EntityTypeDef("doc", [], [new PermissionDef("view", new RelationRef("ghost"))])],
            []);

        await Should.ThrowAsync<SchemaValidationException>(() => _manager.SetActiveSchemaAsync(store, bad));
        (await _manager.GetActiveSchemaAsync(store)).ShouldBeNull();   // nothing activated
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter SchemaManagerTests`
Expected: PASS (2 tests). If the invalid case does not throw, `SchemaValidator` integration is wrong — fix the manager, not the test.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Managers/SchemaManagerTests.cs
git commit -m "test: prove schema manager validates and activates atomically"
```

---

### Task 4: `UsePostgres` registration in the Postgres package

**Files:**
- Create: `src/Relkit.Storage.Postgres/RelkitPostgresBuilderExtensions.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Di/UsePostgresTests.cs`

**Interfaces:**
- Produces: `RelkitBuilder UsePostgres(this RelkitBuilder builder, string connectionString)` registering: `NpgsqlUnitOfWorkFactory` (and as `IUnitOfWorkFactory`), the four Npgsql stores (and as their interfaces), `PostgresCacheStore` as `ICacheStore`, `AuditedWritePath`, the four managers (and as their interfaces), and `NpgsqlCteAuthorizer` as `IAuthorizer` (the CTE primary path). The `IConditionEvaluator` is resolved from DI (the consumer registers `m0/06`'s adapter or a `NullConditionEvaluator`); a `NullConditionEvaluator` is registered as the default if none is present.
- Consumes: `RelkitBuilder` (`m1/09` Task 1, `Relkit.Extensions.DependencyInjection`); the Postgres stores/managers; `IConditionEvaluator`/`NullConditionEvaluator` (`m0/05`).

> **Caching is deferred to the contract-gap resolution.** `m0/08`'s `CachingAuthorizer` constructor takes a concrete `EngineDrivenAuthorizer` (verified: `CachingAuthorizer(EngineDrivenAuthorizer inner, ISchemaStore, ICacheStore, TimeSpan?)`) and calls its internal `CheckInternalAsync` for the unconditioned-only cache rule. `NpgsqlCteAuthorizer` is a different concrete type with no such public/internal seam yet, so it **cannot** be passed to `CachingAuthorizer` today — wrapping it would not compile. Therefore `UsePostgres` registers `NpgsqlCteAuthorizer` **directly** as `IAuthorizer` (correct, just uncached cross-request). The caching wrap is gated on the contract-gap resolution below (surface a shared cacheability seam both authorizers implement); once that lands, change the `IAuthorizer` registration to wrap the CTE authorizer in `CachingAuthorizer`. Read-your-writes and per-request memoization still hold; only the cross-request `ICacheStore` cache is deferred.

> **Reference direction.** `UsePostgres` lives in `Relkit.Storage.Postgres`, which references `Relkit.Extensions.DependencyInjection` (for `RelkitBuilder`) and `Microsoft.Extensions.DependencyInjection.Abstractions`. The DI base project does **not** reference Postgres — so a consumer adds the Postgres package only when calling `.UsePostgres(...)`. No cycle.

- [ ] **Step 1: Add the DI references to the Postgres project**

Run:
```bash
dotnet add src/Relkit.Storage.Postgres reference src/Relkit.Extensions.DependencyInjection
dotnet add src/Relkit.Storage.Postgres package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add tests/Relkit.Storage.Postgres.Tests package Microsoft.Extensions.DependencyInjection
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Di/UsePostgresTests.cs
using Microsoft.Extensions.DependencyInjection;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Extensions.DependencyInjection;
using Relkit.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Di;

[Collection("postgres")]
public class UsePostgresTests(PostgresFixture fx)
{
    [Fact]
    public void UsePostgres_registers_authorizer_and_managers()
    {
        var services = new ServiceCollection();
        services.AddRelkit()
            .UsePostgres(fx.ConnectionString)
            .UseSchema(new SchemaBuilder("v1")
                .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))));

        var provider = services.BuildServiceProvider();
        provider.GetService<IAuthorizer>().ShouldNotBeNull();
        provider.GetService<IRelationManager>().ShouldNotBeNull();
        provider.GetService<ISchemaManager>().ShouldNotBeNull();
        provider.GetService<IStoreManager>().ShouldNotBeNull();
        provider.GetService<ITenantManager>().ShouldNotBeNull();
    }

    [Fact]
    public void UsePostgres_authorizer_is_the_cte_primary_path()
    {
        var services = new ServiceCollection();
        services.AddRelkit().UsePostgres(fx.ConnectionString);
        var provider = services.BuildServiceProvider();
        // The CTE path is registered directly as IAuthorizer; the cross-request cache wrap is deferred
        // to the contract-gap resolution (CachingAuthorizer requires a cacheability seam, see below).
        provider.GetRequiredService<IAuthorizer>().ShouldBeOfType<NpgsqlCteAuthorizer>();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter UsePostgresTests`
Expected: FAIL — `UsePostgres` does not exist.

- [ ] **Step 4: Implement `UsePostgres`**

```csharp
// src/Relkit.Storage.Postgres/RelkitPostgresBuilderExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Relkit.Abstractions;
using Relkit.Core.Conditions;
using Relkit.Extensions.DependencyInjection;
using Relkit.Storage.Postgres.Managers;

namespace Relkit.Storage.Postgres;

public static class RelkitPostgresBuilderExtensions
{
    /// <summary>Register the Postgres provider: stores, CTE authorizer, managers, and cache.</summary>
    public static RelkitBuilder UsePostgres(this RelkitBuilder builder, string connectionString)
    {
        var s = builder.Services;

        // Unit of work.
        s.TryAddSingleton(new NpgsqlUnitOfWorkFactory(connectionString));
        s.TryAddSingleton<IUnitOfWorkFactory>(sp => sp.GetRequiredService<NpgsqlUnitOfWorkFactory>());

        // Stores (concrete + interface).
        s.TryAddSingleton(new NpgsqlRelationStore(connectionString));
        s.TryAddSingleton<IRelationStore>(sp => sp.GetRequiredService<NpgsqlRelationStore>());
        s.TryAddSingleton(new NpgsqlSchemaStore(connectionString));
        s.TryAddSingleton<ISchemaStore>(sp => sp.GetRequiredService<NpgsqlSchemaStore>());
        s.TryAddSingleton(new NpgsqlAttributeStore(connectionString));
        s.TryAddSingleton<IAttributeStore>(sp => sp.GetRequiredService<NpgsqlAttributeStore>());
        s.TryAddSingleton(new NpgsqlChangeLogStore(connectionString));
        s.TryAddSingleton<IChangeLogStore>(sp => sp.GetRequiredService<NpgsqlChangeLogStore>());

        // Condition evaluator: default to the null evaluator unless a consumer registered the m0/06 adapter.
        s.TryAddSingleton<IConditionEvaluator, NullConditionEvaluator>();

        // Cache store. Its (store,tenant) scope is per-tenant; the cache is constructed lazily by the
        // caching layer for each tenant. Here we register a factory keyed by the active tenant via a
        // small per-tenant cache provider. For the unscoped DI registration we register a default-scope
        // cache used by AuditedWritePath's epoch bump (which re-scopes per call through the same table).
        s.TryAddSingleton<ICacheStore>(_ => new PostgresCacheStore(connectionString, default));

        // Audited write path (sequencer) + managers.
        s.TryAddSingleton(sp => new AuditedWritePath(
            sp.GetRequiredService<NpgsqlRelationStore>(),
            sp.GetRequiredService<NpgsqlAttributeStore>(),
            sp.GetRequiredService<NpgsqlSchemaStore>(),
            sp.GetRequiredService<NpgsqlChangeLogStore>(),
            sp.GetRequiredService<ICacheStore>()));

        s.TryAddSingleton<IRelationManager>(sp => new RelkitRelationManager(
            sp.GetRequiredService<NpgsqlUnitOfWorkFactory>(),
            sp.GetRequiredService<NpgsqlRelationStore>(),
            sp.GetRequiredService<NpgsqlAttributeStore>(),
            sp.GetRequiredService<NpgsqlChangeLogStore>(),
            sp.GetRequiredService<AuditedWritePath>()));
        s.TryAddSingleton<ISchemaManager>(sp => new RelkitSchemaManager(
            sp.GetRequiredService<NpgsqlUnitOfWorkFactory>(),
            sp.GetRequiredService<NpgsqlSchemaStore>(),
            sp.GetRequiredService<AuditedWritePath>()));
        s.TryAddSingleton<IStoreManager>(_ => new RelkitStoreManager(connectionString));
        s.TryAddSingleton<ITenantManager>(_ => new RelkitTenantManager(connectionString));

        // Authorizer: the CTE primary path, registered directly as IAuthorizer. The cross-request
        // CachingAuthorizer wrap is deferred to the contract-gap resolution (it needs a cacheability
        // seam NpgsqlCteAuthorizer does not yet expose — see Contract gaps).
        s.TryAddSingleton<NpgsqlCteAuthorizer>(sp => new NpgsqlCteAuthorizer(
            connectionString,
            sp.GetRequiredService<NpgsqlSchemaStore>(),
            sp.GetRequiredService<NpgsqlAttributeStore>(),
            sp.GetRequiredService<IConditionEvaluator>()));
        s.TryAddSingleton<IAuthorizer>(sp => sp.GetRequiredService<NpgsqlCteAuthorizer>());

        return builder;
    }
}
```

> **Once the cacheability seam lands** (Contract gaps below), change the final registration to
> `s.TryAddSingleton<IAuthorizer>(sp => new CachingAuthorizer(sp.GetRequiredService<NpgsqlCteAuthorizer>(), sp.GetRequiredService<NpgsqlSchemaStore>(), sp.GetRequiredService<ICacheStore>()));`
> — a one-line change. Until then the CTE path serves checks directly (correct, with per-request memoization and read-your-writes; only the cross-request cache is absent).

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter UsePostgresTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Storage.Postgres tests/Relkit.Storage.Postgres.Tests
git commit -m "feat: add UsePostgres DI registration for stores, managers, and cached authorizer"
```

---

### Task 5: `AddRelkitInstrumentation()` OpenTelemetry wiring

**Files:**
- Create: `src/Relkit.Extensions.DependencyInjection/RelkitInstrumentationExtensions.cs`
- Test: `tests/Relkit.Extensions.DependencyInjection.Tests/InstrumentationTests.cs`

**Interfaces:**
- Produces: `TracerProviderBuilder AddRelkitInstrumentation(this TracerProviderBuilder builder)` enabling the `"Relkit"` `ActivitySource`, and `MeterProviderBuilder AddRelkitInstrumentation(this MeterProviderBuilder builder)` enabling the `"Relkit"` `Meter` (both from `RelkitDiagnostics`, `m0/01`). A consumer chains these into their existing `AddOpenTelemetry().WithTracing(...)`/`WithMetrics(...)`.
- Consumes: `RelkitDiagnostics.Name` (`m0/01`); the OpenTelemetry builder types.

- [ ] **Step 1: Add the OpenTelemetry package and write the failing test**

Run:
```bash
dotnet add src/Relkit.Extensions.DependencyInjection package OpenTelemetry
dotnet add tests/Relkit.Extensions.DependencyInjection.Tests package OpenTelemetry
```

```csharp
// tests/Relkit.Extensions.DependencyInjection.Tests/InstrumentationTests.cs
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Relkit.Abstractions;
using Relkit.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Relkit.Extensions.DependencyInjection.Tests;

public class InstrumentationTests
{
    [Fact]
    public void AddRelkitInstrumentation_subscribes_the_relkit_activity_source()
    {
        var exported = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddRelkitInstrumentation()
            .AddInMemoryExporter(exported)
            .Build();

        using (var activity = RelkitDiagnostics.ActivitySource.StartActivity("relkit.check"))
            activity?.SetTag("test", "1");

        exported.ShouldContain(a => a.DisplayName == "relkit.check");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Extensions.DependencyInjection.Tests --filter InstrumentationTests`
Expected: FAIL — `AddRelkitInstrumentation` does not exist.

- [ ] **Step 3: Implement the instrumentation extensions**

```csharp
// src/Relkit.Extensions.DependencyInjection/RelkitInstrumentationExtensions.cs
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Relkit.Abstractions;

namespace Relkit.Extensions.DependencyInjection;

/// <summary>
/// Wires Relkit's <c>"Relkit"</c> ActivitySource and Meter (from <see cref="RelkitDiagnostics"/>,
/// m0/01) into OpenTelemetry (spec §11.4). Chain into AddOpenTelemetry().WithTracing/WithMetrics.
/// </summary>
public static class RelkitInstrumentationExtensions
{
    public static TracerProviderBuilder AddRelkitInstrumentation(this TracerProviderBuilder builder)
        => builder.AddSource(RelkitDiagnostics.Name);

    public static MeterProviderBuilder AddRelkitInstrumentation(this MeterProviderBuilder builder)
        => builder.AddMeter(RelkitDiagnostics.Name);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Extensions.DependencyInjection.Tests --filter InstrumentationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Extensions.DependencyInjection tests/Relkit.Extensions.DependencyInjection.Tests
git commit -m "feat: add AddRelkitInstrumentation OpenTelemetry wiring"
```

---

### Task 6: End-to-end sample — define schema → write tuples → Check/ListObjects

**Files:**
- Test: `tests/Relkit.Storage.Postgres.Tests/Di/EndToEndSampleTests.cs`

**Interfaces:**
- Consumes: the full DI surface (`AddRelkit().UsePostgres().UseSchema()`), the managers, and `IAuthorizer`. Proves the wired-up library works against real Postgres: provision store + tenant, activate a schema, write tuples through `IRelationManager` (audited + epoch-bumped), then `Check` and `ListObjects` through `IAuthorizer` return the expected answers. This is the acceptance test that the Blazor app's adoption path (spec §11.2 "the Blazor application adopts the engine here") rests on.

- [ ] **Step 1: Write the end-to-end test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Di/EndToEndSampleTests.cs
using Microsoft.Extensions.DependencyInjection;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Extensions.DependencyInjection;
using Relkit.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Di;

[Collection("postgres")]
public class EndToEndSampleTests(PostgresFixture fx) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Define_schema_write_tuples_then_check_and_list()
    {
        // 12.2-flavoured schema: a team grant over a curated species list.
        var schemaBuilder = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("species", t => t
                .Relation("editor", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("editor")));

        var services = new ServiceCollection();
        services.AddRelkit().UsePostgres(fx.ConnectionString).UseSchema(schemaBuilder);
        var provider = services.BuildServiceProvider();

        var stores = provider.GetRequiredService<IStoreManager>();
        var tenants = provider.GetRequiredService<ITenantManager>();
        var schemas = provider.GetRequiredService<ISchemaManager>();
        var relations = provider.GetRequiredService<IRelationManager>();
        var authorizer = provider.GetRequiredService<IAuthorizer>();

        const string store = "e2e";
        var t = new TenantContext(store, "tenant-1");

        await stores.CreateStoreAsync(store);
        await tenants.CreateTenantAsync(t);
        await tenants.CreateTenantAsync(new TenantContext(store, store));   // schema-audit bookkeeping tenant
        await schemas.SetActiveSchemaAsync(store, schemaBuilder.Build());

        await relations.WriteTuplesAsync(t, "zoo-admin",
        [
            new RelationTuple(new EntityRef("species", "kangaroo"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("species", "wallaby"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("group", "macropods"), "member", new SubjectRef("user", "alice")),
        ]);

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>());

        // Check: alice may edit kangaroo (via the macropods round).
        var check = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("species", "kangaroo"), "edit", new SubjectRef("user", "alice"), ctx));
        check.Allowed.ShouldBeTrue();

        // ListObjects: the species alice may edit.
        var list = await authorizer.ListObjectsAsync(new ListObjectsRequest(
            t, new SubjectRef("user", "alice"), "species", "edit", ctx));
        list.ObjectIds.OrderBy(x => x).ShouldBe(["kangaroo", "wallaby"]);

        // The audit trail recorded who granted access.
        var log = await relations.ReadChangeLogAsync(t, new ChangeLogFilter());
        log.ShouldContain(e => e.Actor == "zoo-admin" && e.Operation == "write");
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter EndToEndSampleTests`
Expected: PASS — the fully wired library defines a schema, writes audited tuples, and answers Check/ListObjects over real Postgres.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Di/EndToEndSampleTests.cs
git commit -m "test: end-to-end DI sample over Testcontainers Postgres"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] `AddRelkit().UsePostgres(conn).UseSchema(builder)` resolves `IAuthorizer`, `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager` (Tasks 1, 4).
- [ ] The managers supply the caller's `actor` and before/after diffs, and call `AuditedWritePath` so the data write + `change_log` + epoch bump commit in one unit of work (Task 2).
- [ ] An invalid schema throws `SchemaValidationException` and activates nothing; a valid one activates + audits atomically (Task 3).
- [ ] `IAuthorizer` resolves to `NpgsqlCteAuthorizer` (the CTE primary path); the `CachingAuthorizer` wrap is deferred to the cacheability-seam contract gap, with the one-line follow-up registration documented (Task 4).
- [ ] `AddRelkitInstrumentation()` subscribes the `"Relkit"` ActivitySource and Meter into OTel (Task 5).
- [ ] The end-to-end sample defines a schema, writes audited tuples, and answers Check + ListObjects over real Postgres, with the audit trail recording the actor (Task 6).

## Contract gaps (reported, not changed)

- **`CachingAuthorizer` requires a concrete inner with `CheckInternalAsync`.** `m0/08`'s `CachingAuthorizer` takes a concrete `EngineDrivenAuthorizer` and calls its internal `CheckInternalAsync` returning `(bool Allowed, bool ConditionTouched)` so it never caches condition-dependent results. `NpgsqlCteAuthorizer` (m1/05) currently exposes only the public `IAuthorizer.CheckAsync`. To cache the CTE path safely with the unconditioned-only rule, **`NpgsqlCteAuthorizer` must expose an equivalent internal `CheckInternalAsync(CheckRequest) -> (bool, bool)`** (it already tracks `ConditionTouched` via the reused `EvalContext` — surfacing it is mechanical) **and `CachingAuthorizer` must accept it** (either via a shared `ICacheableAuthorizer` interface or a second constructor overload). This is a real contract/seam gap touching `m0/08` and `m1/05`. The `UsePostgres` registration (Task 4) notes the interim: until the signal is surfaced, the cache wraps the CTE authorizer conservatively. **Recommended resolution (for `README.md` + `m0/08`/`m1/05`):** add an `internal (bool Allowed, bool ConditionTouched) ` async check method to both authorizers behind a small shared `Relkit.Core` interface, and have `CachingAuthorizer` depend on that interface. Not changed here per the hard rules; flagged for the contract owner.
- **Manager home is the Postgres package, not Core.** The contract's architecture diagram (spec §4) sketches managers in `Relkit.Core`. Because `AuditedWritePath` (the in-transaction sequencer, owned by `m1/07`) lives in `Relkit.Storage.Postgres` and the managers must call it within an `NpgsqlUnitOfWork`, the concrete managers are registered from `Relkit.Storage.Postgres` here. They implement the `Relkit.Abstractions` interfaces, so consumers are unaffected. If the contract owner prefers managers in Core, Core would need an abstract sequencing primitive (an `IAuditedWritePath` in Abstractions) that the Postgres provider implements — a larger refactor flagged for visibility.
- **`PostgresCacheStore` tenant scope in DI.** `PostgresCacheStore` is constructed with a fixed `(store, tenant)` scope (`m1/07`), but the DI registration is process-wide. The epoch methods (`GetEpochAsync(t)`/`BumpEpochAsync(t)`) take the tenant explicitly and ignore the construction scope, so the epoch path is correct regardless; only `GetAsync/SetAsync` (the entry cache) use the construction scope. For the cross-request check cache to be tenant-correct, the cache must be resolved per `TenantContext`. The interim registration uses a `default` scope for the shared epoch operations the `CachingAuthorizer` and `AuditedWritePath` need; a per-tenant cache factory is the clean fix (flagged for `m2/05`, which refines the cache). Not changed here.
```

