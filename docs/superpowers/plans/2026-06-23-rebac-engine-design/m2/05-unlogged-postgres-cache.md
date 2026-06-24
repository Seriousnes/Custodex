# M2/05 — Hardening the UNLOGGED Postgres Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the existing `PostgresCacheStore` (built in `m1/07` over the `UNLOGGED` `cache_entries` table with epoch + basic get/set + lazy expiry) for production use across horizontally scaled instances. Add (1) a **per-tenant scoping fix** — `m1/07` constructed the store with a fixed `(store, tenant)` scope but it is registered process-wide, so introduce a `PostgresCacheStoreFactory` that yields a correctly scoped store per `TenantContext`; (2) a **background TTL sweep job** (`CacheSweepService`) that deletes expired `cache_entries` rows on an interval, complementing the lazy read-time expiry; and (3) document the **`CacheValueCodec` integration** (`m0/08`) as the value (de)serialization boundary the caching layer already owns, with a guard test proving a `bool` decision round-trips through `SetAsync`/`GetAsync` and the codec.

**Architecture:** `m1/07` left `PostgresCacheStore(connectionString, scope)` correct for epochs and entries but scoped to a single `(store, tenant)` at construction — wrong for a multi-tenant process that registers one cache in DI. This plan adds `PostgresCacheStoreFactory(connectionString)` with `ICacheStore For(TenantContext tenant)`, so each request resolves a store scoped to its tenant; the underlying `cache_entries` rows remain keyed by `(store_id, tenant_id, key)` so two store instances for the same tenant share the same rows (shared-cache hit across instances). The epoch methods already take an explicit `TenantContext`, so they are unaffected; only `GetAsync`/`SetAsync` (which carry just `key`) needed the scope, and the factory supplies it. `CacheSweepService : BackgroundService` periodically runs `DELETE FROM cache_entries WHERE expires_at <= now()` over the whole table (sweep is store/tenant-agnostic; the `ix_cache_entries_expiry` index from `m1/01` makes it cheap), keeping the `UNLOGGED` table from accumulating dead rows that lazy expiry alone never reclaims. `CacheValueCodec` (`m0/08`) is the value boundary: `CachingAuthorizer` encodes the `bool` decision before `SetAsync` and decodes after `GetAsync`; this plan keeps the cache store value-opaque (`byte[]`) and proves the codec round-trips through the Postgres store.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, `Microsoft.Extensions.Hosting` (`BackgroundService`), xUnit, Shouldly, `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings. Depends on `m0/01` (`ICacheStore`, `CacheEntry`), `m0/08` (`CacheValueCodec`, `CachingAuthorizer` — the value boundary), `m1/01` (`cache_entries` UNLOGGED table + `ix_cache_entries_expiry`, `MigrationRunner`, `PostgresFixture`), `m1/03` (`NpgsqlUnitOfWorkFactory`/`NpgsqlUnitOfWork` for seeding), `m1/07` (the existing `PostgresCacheStore` — **do not rebuild it**; this plan adds the factory, sweep, and codec wiring around it).

## Shared decisions (locked)

- **The existing `PostgresCacheStore` is kept as-is.** `m1/07` Task 1 (epoch get/bump) and Task 2 (`cache_entries` get/set with epoch stamp + lazy expiry) are correct and untouched. This plan only **adds** the factory, the sweep service, and the codec round-trip guard.
- **Per-tenant scoping** is solved by a factory, not by changing the `ICacheStore` method signatures (which `m0/08`'s `CachingAuthorizer` already calls with the `key`-only `GetAsync`/`SetAsync` and the `TenantContext`-carrying epoch methods). The factory yields one `ICacheStore` per `TenantContext`.
- **The sweep is whole-table.** Expired rows across all tenants are reclaimed in one `DELETE`; the `expires_at` index makes it index-only on the predicate. The sweep is correctness-neutral (lazy expiry already hides expired rows from reads) — it is purely space reclamation for the `UNLOGGED` table.
- **`CacheValueCodec` stays in `m0/08`.** The Postgres store remains value-opaque. This plan does not move or duplicate the codec; it documents and tests the boundary.

---

### Task 1: `PostgresCacheStoreFactory` — per-tenant scoped stores over shared rows

**Files:**
- Create: `src/Custodex.Storage.Postgres/PostgresCacheStoreFactory.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Cache/PostgresCacheStoreFactoryTests.cs`

**Interfaces:**
- Produces: `PostgresCacheStoreFactory(string connectionString)` with `ICacheStore For(TenantContext tenant)` returning a `PostgresCacheStore` scoped to that tenant. The factory is the process-wide DI singleton; per-request code calls `For(tenant)`. Because `cache_entries` rows are keyed by `(store_id, tenant_id, key)`, two stores built by the factory for the same tenant read and write the same rows — a shared cache across instances.
- Consumes: `PostgresCacheStore(connectionString, scope)` (`m1/07`), `TenantContext`, `ICacheStore` (`m0/01`).

> **Why a factory, not a mutable scope.** `m1/07`'s store binds `(store, tenant)` at construction so the `key`-only `GetAsync`/`SetAsync` can hard-filter on the tenant — defence in depth (spec §6.3). Registering one such store process-wide would pin every tenant's reads to one tenant's rows. The factory keeps the per-tenant hard filter while letting a single registration serve all tenants: resolve `For(tenant)` per request. This is the standard "scoped resource from a singleton factory" shape and avoids any change to the locked `ICacheStore` contract.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cache/PostgresCacheStoreFactoryTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class PostgresCacheStoreFactoryTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Two_store_instances_for_the_same_tenant_share_rows()
    {
        var t = new TenantContext("zoo", "shared");
        await SeedTenantAsync(t);
        var factory = new PostgresCacheStoreFactory(fx.ConnectionString);

        var writer = factory.For(t);
        var reader = factory.For(t);   // a second, independent instance — same tenant scope

        await writer.SetAsync("k", new CacheEntry([7], Epoch: 3), TimeSpan.FromMinutes(5));
        var got = await reader.GetAsync("k");

        got.ShouldNotBeNull();
        got!.Value.ShouldBe(new byte[] { 7 });
        got.Epoch.ShouldBe(3);
    }

    [Fact]
    public async Task Different_tenants_are_isolated()
    {
        var a = new TenantContext("zoo", "tenant-a");
        var b = new TenantContext("zoo", "tenant-b");
        await SeedTenantAsync(a);
        await SeedTenantAsync(b);
        var factory = new PostgresCacheStoreFactory(fx.ConnectionString);

        await factory.For(a).SetAsync("k", new CacheEntry([1], Epoch: 1), TimeSpan.FromMinutes(5));

        (await factory.For(b).GetAsync("k")).ShouldBeNull();   // tenant b never sees tenant a's row
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter PostgresCacheStoreFactoryTests`
Expected: FAIL — `PostgresCacheStoreFactory` does not exist.

- [ ] **Step 3: Implement the factory**

```csharp
// src/Custodex.Storage.Postgres/PostgresCacheStoreFactory.cs
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Process-wide factory for tenant-scoped <see cref="PostgresCacheStore"/> instances. Registered once
/// in DI; per-request code calls <see cref="For"/> to get a store hard-filtered to its tenant. All
/// stores read/write the shared <c>cache_entries</c> rows keyed by <c>(store_id, tenant_id, key)</c>,
/// so instances on different nodes share one cache (spec §9.1).
/// </summary>
public sealed class PostgresCacheStoreFactory(string connectionString)
{
    public ICacheStore For(TenantContext tenant) => new PostgresCacheStore(connectionString, tenant);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter PostgresCacheStoreFactoryTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres/PostgresCacheStoreFactory.cs tests/Custodex.Storage.Postgres.Tests/Cache/PostgresCacheStoreFactoryTests.cs
git commit -m "feat: add per-tenant Postgres cache-store factory over shared rows"
```

---

### Task 2: `CacheSweep` — delete expired rows (the swept-TTL primitive)

**Files:**
- Create: `src/Custodex.Storage.Postgres/CacheSweep.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Cache/CacheSweepTests.cs`

**Interfaces:**
- Produces: `static Task<int> CacheSweep.RunAsync(string connectionString, CancellationToken ct = default)` — deletes every `cache_entries` row whose `expires_at <= now()` across all tenants and returns the number of rows reclaimed. This is the unit of work the background service (Task 3) invokes on a schedule; isolating it makes it testable without a hosted service.
- Consumes: `cache_entries` (`m1/01`); the `ix_cache_entries_expiry` index makes the predicate cheap.

> **Why a separate sweep beyond lazy expiry.** Lazy expiry (`m1/07`: `GetAsync` filters `expires_at > now()`) hides expired rows from reads but never deletes them. On an `UNLOGGED` table under steady write churn that leaks space and bloats the index. The sweep reclaims it. Sweeping is **correctness-neutral**: a row deleted by the sweep was already invisible to reads, so a concurrent reader never observes a difference. No tenant filter is needed — expiry is global.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cache/CacheSweepTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class CacheSweepTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private readonly TenantContext _t = new("zoo", "sweep");

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<long> RowCountAsync()
    {
        await using var conn = await fx.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM cache_entries WHERE store_id = @s AND tenant_id = @t",
            new { s = _t.Store, t = _t.Tenant });
    }

    [Fact]
    public async Task Sweep_removes_expired_rows_and_keeps_live_ones()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        await cache.SetAsync("live", new CacheEntry([1], Epoch: 1), TimeSpan.FromMinutes(10));
        await cache.SetAsync("dead", new CacheEntry([2], Epoch: 1), TimeSpan.FromMilliseconds(-1));   // already expired

        (await RowCountAsync()).ShouldBe(2);   // both physically present (lazy expiry does not delete)

        var reclaimed = await CacheSweep.RunAsync(fx.ConnectionString);
        reclaimed.ShouldBeGreaterThanOrEqualTo(1);

        (await RowCountAsync()).ShouldBe(1);                 // dead row gone
        (await cache.GetAsync("live")).ShouldNotBeNull();    // live row untouched
        (await cache.GetAsync("dead")).ShouldBeNull();
    }

    [Fact]
    public async Task Sweep_on_a_clean_table_reclaims_nothing()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        await cache.SetAsync("only-live", new CacheEntry([3], Epoch: 1), TimeSpan.FromMinutes(10));

        var before = await RowCountAsync();
        await CacheSweep.RunAsync(fx.ConnectionString);
        (await RowCountAsync()).ShouldBe(before);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CacheSweepTests`
Expected: FAIL — `CacheSweep` does not exist.

- [ ] **Step 3: Implement the sweep**

```csharp
// src/Custodex.Storage.Postgres/CacheSweep.cs
using Dapper;
using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Reclaims expired rows from the UNLOGGED <c>cache_entries</c> table. Complements the lazy read-time
/// expiry in <see cref="PostgresCacheStore"/>: lazy expiry hides expired rows from reads, the sweep
/// deletes them so the table and its <c>expires_at</c> index do not bloat (spec §9.1). Correctness-
/// neutral — swept rows were already invisible to readers.
/// </summary>
public static class CacheSweep
{
    public static async Task<int> RunAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM cache_entries WHERE expires_at <= now()", cancellationToken: ct));
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CacheSweepTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres/CacheSweep.cs tests/Custodex.Storage.Postgres.Tests/Cache/CacheSweepTests.cs
git commit -m "feat: add expired-row sweep for the unlogged cache table"
```

---

### Task 3: `CacheSweepService` — the background TTL sweep job

**Files:**
- Modify: `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj` (add `Microsoft.Extensions.Hosting.Abstractions`)
- Create: `src/Custodex.Storage.Postgres/CacheSweepOptions.cs`
- Create: `src/Custodex.Storage.Postgres/CacheSweepService.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Cache/CacheSweepServiceTests.cs`

**Interfaces:**
- Produces: `CacheSweepOptions { string ConnectionString; TimeSpan Interval = 5 min }` and `CacheSweepService(CacheSweepOptions options) : BackgroundService` running `CacheSweep.RunAsync` every `Interval` until the host stops, recording each sweep's reclaimed-row count to `CustodexDiagnostics` (a new `Custodex.cache.swept` counter) for the spec §11.4 observability surface.
- Consumes: `CacheSweep.RunAsync` (Task 2); `BackgroundService`/`IHostedService` from `Microsoft.Extensions.Hosting.Abstractions`; `CustodexDiagnostics.Meter` (`m0/01`).

> **Hosting dependency.** `BackgroundService` lives in `Microsoft.Extensions.Hosting.Abstractions` (a Microsoft package, MIT — permitted under the README license rule). The service is opt-in: it is only registered when the consuming app uses the Postgres cache and wants automatic sweeping (wired in `m1/09`'s DI extension as `AddHostedService<CacheSweepService>()` when configured). The store works without it (lazy expiry keeps reads correct); the service only reclaims space.

> **New diagnostic instrument.** `Custodex.cache.swept` (a `Counter<long>`) is added to `CustodexDiagnostics` so the sweep's reclaimed-row count is observable alongside `Custodex.cache.hits`/`misses` (spec §11.4). Adding an instrument to the existing `CustodexDiagnostics.Meter` is additive and does not change any contract type; it is flagged in this plan's return as an addition to the `m0/01` diagnostics holder.

- [ ] **Step 1: Add the hosting package**

Run:
```bash
dotnet add src/Custodex.Storage.Postgres package Microsoft.Extensions.Hosting.Abstractions
```

- [ ] **Step 2: Add the `Custodex.cache.swept` counter to `CustodexDiagnostics`**

Add to `src/Custodex.Abstractions/CustodexDiagnostics.cs` (alongside `CacheHits`/`CacheMisses`):

```csharp
    public static readonly Counter<long> CacheSwept = Meter.CreateCounter<long>("Custodex.cache.swept");
```

- [ ] **Step 3: Write the failing test**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cache/CacheSweepServiceTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class CacheSweepServiceTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private readonly TenantContext _t = new("zoo", "sweep-svc");

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<long> RowCountAsync()
    {
        await using var conn = await fx.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM cache_entries WHERE store_id = @s AND tenant_id = @t",
            new { s = _t.Store, t = _t.Tenant });
    }

    [Fact]
    public async Task Service_sweeps_expired_rows_on_its_interval()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        await cache.SetAsync("dead", new CacheEntry([1], Epoch: 1), TimeSpan.FromMilliseconds(-1));
        (await RowCountAsync()).ShouldBe(1);

        var options = new CacheSweepOptions
        {
            ConnectionString = fx.ConnectionString,
            Interval = TimeSpan.FromMilliseconds(50)
        };
        var service = new CacheSweepService(options);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Poll until the periodic sweep reclaims the expired row (bounded so the test cannot hang).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await RowCountAsync() > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        await service.StopAsync(cts.Token);
        (await RowCountAsync()).ShouldBe(0);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CacheSweepServiceTests`
Expected: FAIL — `CacheSweepOptions`/`CacheSweepService` do not exist.

- [ ] **Step 5: Implement the options and service**

```csharp
// src/Custodex.Storage.Postgres/CacheSweepOptions.cs
namespace Custodex.Storage.Postgres;

/// <summary>Configuration for the background <c>cache_entries</c> TTL sweep (spec §9.1).</summary>
public sealed class CacheSweepOptions
{
    public required string ConnectionString { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);
}
```

```csharp
// src/Custodex.Storage.Postgres/CacheSweepService.cs
using Microsoft.Extensions.Hosting;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Background TTL sweep for the UNLOGGED <c>cache_entries</c> table. Runs <see cref="CacheSweep.RunAsync"/>
/// every <see cref="CacheSweepOptions.Interval"/> until the host stops, reporting reclaimed rows to
/// <see cref="CustodexDiagnostics.CacheSwept"/> (spec §9.1 / §11.4). Opt-in: registered via the DI
/// extension only when automatic sweeping is wanted; the store stays correct without it (lazy expiry).
/// </summary>
public sealed class CacheSweepService(CacheSweepOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                var reclaimed = await CacheSweep.RunAsync(options.ConnectionString, stoppingToken);
                if (reclaimed > 0)
                    CustodexDiagnostics.CacheSwept.Add(reclaimed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // host stopping
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
```

> The first sweep runs immediately on start (the `do/while` body executes before the first `WaitForNextTickAsync`), so the test's 50 ms interval reclaims promptly; production uses the 5-minute default.

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CacheSweepServiceTests`
Expected: PASS (1 test).

- [ ] **Step 7: Commit**

```bash
git add src/Custodex.Storage.Postgres/CacheSweepOptions.cs src/Custodex.Storage.Postgres/CacheSweepService.cs src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj src/Custodex.Abstractions/CustodexDiagnostics.cs tests/Custodex.Storage.Postgres.Tests/Cache/CacheSweepServiceTests.cs
git commit -m "feat: add background TTL sweep service for the unlogged cache"
```

---

### Task 4: `CacheValueCodec` round-trip through the Postgres store (epoch-mismatch is a miss)

**Files:**
- Test: `tests/Custodex.Storage.Postgres.Tests/Cache/PostgresCacheCodecTests.cs`

**Interfaces:**
- Produces: a guard test proving the `m0/08` `CacheValueCodec` round-trips a `bool` decision through `PostgresCacheStore.SetAsync`/`GetAsync` (the value boundary the `CachingAuthorizer` owns), and that an **epoch mismatch reads as a miss** at the caching-layer level — exactly how `CachingAuthorizer` (`m0/08`) treats a stale stamp. No production code: this pins the integration `m0/08` + `m1/07` + this plan rely on.
- Consumes: `CacheValueCodec` (`m0/08`, in `Custodex.Core.Caching`), `PostgresCacheStore` (`m1/07`).

> **Why test the codec here.** `CacheValueCodec` lives in `Custodex.Core` and the Postgres store is value-opaque (`byte[]`), so neither side alone proves the end-to-end value boundary over a real database. This test wires `Encode → SetAsync → GetAsync → Decode` against Testcontainers Postgres and asserts the decision survives, then asserts the epoch-mismatch-is-a-miss rule the `CachingAuthorizer` enforces. It requires the test project to reference `Custodex.Core`.

- [ ] **Step 1: Ensure the test project references `Custodex.Core`** (idempotent — added by `m1/05` if not already)

Run:
```bash
dotnet add tests/Custodex.Storage.Postgres.Tests reference src/Custodex.Core
```

- [ ] **Step 2: Write the test**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cache/PostgresCacheCodecTests.cs
using Custodex.Abstractions;
using Custodex.Core.Caching;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cache;

[Collection("postgres")]
public class PostgresCacheCodecTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private readonly TenantContext _t = new("zoo", "codec");

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Bool_decision_round_trips_through_the_store_via_the_codec()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        var epoch = await cache.GetEpochAsync(_t);

        await cache.SetAsync("check:doc:D1:view:user:alice",
            new CacheEntry(CacheValueCodec.Encode(true), epoch), TimeSpan.FromMinutes(5));

        var entry = await cache.GetAsync("check:doc:D1:view:user:alice");
        entry.ShouldNotBeNull();
        CacheValueCodec.Decode(entry!.Value).ShouldBeTrue();   // decision survives the DB round-trip
    }

    [Fact]
    public async Task Epoch_mismatch_is_treated_as_a_miss_by_the_caching_layer()
    {
        var cache = new PostgresCacheStore(fx.ConnectionString, _t);
        var epoch = await cache.GetEpochAsync(_t);

        // Store stamped at the current epoch.
        await cache.SetAsync("k", new CacheEntry(CacheValueCodec.Encode(false), epoch), TimeSpan.FromMinutes(5));

        // A write bumps the epoch (as the audited write path does, m1/07).
        await using (var u = await _factory.BeginAsync())
        {
            await cache.BumpEpochAsync(_t, u);
            await u.CommitAsync();
        }

        var current = await cache.GetEpochAsync(_t);
        var entry = await cache.GetAsync("k");

        // The row is still physically present (sweep/lazy-expiry are TTL, not epoch), but its stamp is
        // stale; the caching layer (m0/08 CachingAuthorizer) compares stamp to current epoch => miss.
        entry.ShouldNotBeNull();
        (entry!.Epoch == current).ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter PostgresCacheCodecTests`
Expected: PASS (2 tests).

- [ ] **Step 4: Commit**

```bash
git add tests/Custodex.Storage.Postgres.Tests/Cache/PostgresCacheCodecTests.cs tests/Custodex.Storage.Postgres.Tests/Custodex.Storage.Postgres.Tests.csproj
git commit -m "test: assert cache-value codec round-trips and epoch-mismatch is a miss"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] The existing `m1/07` `PostgresCacheStore` is unchanged — this plan only adds the factory, sweep, service, codec test, and one diagnostics counter.
- [ ] `PostgresCacheStoreFactory.For(tenant)` yields a tenant-scoped store; two instances for the same tenant share rows; different tenants are isolated (Task 1).
- [ ] `CacheSweep.RunAsync` deletes expired rows and leaves live ones; the row count drops (Task 2).
- [ ] `CacheSweepService` sweeps on its interval and stops with the host; reclaimed rows feed `Custodex.cache.swept` (Task 3).
- [ ] `CacheValueCodec` round-trips a `bool` decision through the Postgres store; an epoch-mismatch entry is recognised as a miss by the caching layer's stamp comparison (Task 4).

## Contract gaps (reported, not changed)

- **`CustodexDiagnostics.CacheSwept` counter added (additive).** Task 3 adds a `Custodex.cache.swept` `Counter<long>` to the existing `m0/01` `CustodexDiagnostics` holder for the spec §11.4 reverse-index/cache observability surface. This is an additive instrument on the existing `"Custodex"` Meter, not a change to any contract record/interface, so no `README.md` change is required; flagged here for visibility.
- **Hosting dependency on `Microsoft.Extensions.Hosting.Abstractions` (MIT).** `CacheSweepService : BackgroundService` adds this Microsoft package to `Custodex.Storage.Postgres`. It is MIT-licensed (permitted under the README license rule) and the sweep service is opt-in. No `README.md` change made; recorded for the packaging review.
