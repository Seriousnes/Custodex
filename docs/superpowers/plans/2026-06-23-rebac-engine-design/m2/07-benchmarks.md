# M2/07 — BenchmarkDotNet Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `Relkit.Benchmarks` BenchmarkDotNet project measuring **Check** and **ListObjects** latency at representative zoo scale — a Testcontainers Postgres seeded with ~50 users, ~2k objects, and ~20k tuples — comparing the three execution paths (engine-driven **oracle**, Postgres **CTE**, and index-backed **IndexedAuthorizer**) **with and without** the cross-request cache. Document how to run it and read the output (spec §11.1 / §11.4).

**Architecture:** One `Relkit.Benchmarks` console project under `tests/` (per `README.md` layout). A shared `ZooScaleFixture` starts a single Postgres container, applies migrations, seeds the representative dataset once (the seed is deterministic — fixed RNG seed — so runs are comparable), and builds every authorizer variant over it: the in-memory `EngineDrivenAuthorizer` oracle (seeded from the same model), the `NpgsqlCteAuthorizer`, and the `IndexedAuthorizer` (over a populated `reverse_index` rebuilt via `m2/02`). Cache variants wrap the CTE and Indexed paths in `CachingAuthorizer` (`m0/08`) over a `PostgresCacheStore`. BenchmarkDotNet's `[GlobalSetup]` builds the fixture once per process; `[Benchmark]` methods run the hot operation. Two benchmark classes — `CheckBenchmarks` and `ListObjectsBenchmarks` — each parameterised by path (oracle / cte / index) and cache (on / off), measuring a fixed, representative probe so every variant does equivalent work.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, BenchmarkDotNet, Npgsql, Dapper, `Testcontainers.PostgreSql`. References `Relkit.Core`, `Relkit.Storage.Postgres`, `Relkit.Storage.InMemory`.

## Global Constraints

See `../README.md` → Global Constraints. `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 metadata; no EF Core; identifiers non-empty ordinal strings; id `"*"` wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation (the benchmark seed uses a fixed-seed `Random` for reproducibility, never inside evaluation). Depends on `m0/04` (`InMemory*` stores), `m0/05`/`m0/07` (`EngineDrivenAuthorizer`), `m0/08` (`CachingAuthorizer`, `CacheValueCodec`), `m1/01` (`MigrationRunner`, schema), `m1/03`/`m1/04` (Npgsql stores + unit of work), `m1/05`/`m1/06` (`NpgsqlCteAuthorizer`), `m2/01`/`m2/02` (`IIndexStore`/`PostgresIndexStore` + the full rebuild), `m2/04` (`IndexedAuthorizer`), `m2/05` (`PostgresCacheStore`, `PostgresCacheStoreFactory`).

> **Benchmarks are measurement, not correctness.** Equivalence of the three paths is proven by `m1/08` and `m2/06`; this suite assumes that and measures latency. A tiny sanity assertion in `[GlobalSetup]` (all three authorizers agree on one probe) catches a misconfigured fixture early but is not the correctness mechanism.

## Representative scale (spec §9.2 / §11.1 target)

The seeded dataset targets the spec's stated per-tenant scale ("dozens to low-hundreds of users, low-thousands of resources, tens of thousands of tuples"):

- **~50 users**, all members of a few groups (roles/teams) with nested membership two levels deep.
- **~2,000 objects** of type `animal`, each with an `enclosure` structural edge and a `species` edge.
- **~20,000 tuples** total: group memberships, per-object grants, structural edges, plus a slice of conditioned grants and a slice of `user:*` wildcard grants so every path exercises its conditioned-recheck and type-universe branches.

A single tenant under one store; the seed is deterministic so latency numbers are stable across runs.

---

### Task 1: Create the `Relkit.Benchmarks` project

**Files:**
- Create: `tests/Relkit.Benchmarks/Relkit.Benchmarks.csproj`
- Create: `tests/Relkit.Benchmarks/Program.cs`

**Interfaces:**
- Produces: a runnable BenchmarkDotNet console app referencing the engine, the Postgres provider, and the in-memory provider, with a `Program` entry point that dispatches to the benchmark classes via `BenchmarkSwitcher`.

- [ ] **Step 1: Create the project and references**

Run:
```bash
dotnet new console -n Relkit.Benchmarks -o tests/Relkit.Benchmarks -f net10.0
rm tests/Relkit.Benchmarks/Class1.cs 2>/dev/null || true
dotnet sln add tests/Relkit.Benchmarks
dotnet add tests/Relkit.Benchmarks reference src/Relkit.Core
dotnet add tests/Relkit.Benchmarks reference src/Relkit.Storage.Postgres
dotnet add tests/Relkit.Benchmarks reference src/Relkit.Storage.InMemory
dotnet add tests/Relkit.Benchmarks package BenchmarkDotNet
dotnet add tests/Relkit.Benchmarks package Testcontainers.PostgreSql
dotnet add tests/Relkit.Benchmarks package Npgsql
dotnet add tests/Relkit.Benchmarks package Dapper
```

- [ ] **Step 2: Force a Release-only, optimized build for the benchmark project**

Add to `tests/Relkit.Benchmarks/Relkit.Benchmarks.csproj` inside `<Project>` (BenchmarkDotNet refuses to run a non-optimized build; `TreatWarningsAsErrors` from `Directory.Build.props` still applies):

```xml
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <ServerGarbageCollection>true</ServerGarbageCollection>
  </PropertyGroup>
```

- [ ] **Step 3: Write the entry point**

```csharp
// tests/Relkit.Benchmarks/Program.cs
using BenchmarkDotNet.Running;

namespace Relkit.Benchmarks;

public static class Program
{
    // Dispatches to a benchmark class by name, e.g.:
    //   dotnet run -c Release --project tests/Relkit.Benchmarks -- --filter *CheckBenchmarks*
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
```

- [ ] **Step 4: Verify the project builds and lists benchmarks**

Run: `dotnet run -c Release --project tests/Relkit.Benchmarks -- --list flat`
Expected: builds clean; prints an empty (or, after later tasks, populated) benchmark list without error.

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Benchmarks Relkit.sln
git commit -m "chore: scaffold Relkit.Benchmarks project"
```

---

### Task 2: `ZooScaleFixture` — seed the representative dataset and build every authorizer

**Files:**
- Create: `tests/Relkit.Benchmarks/ZooScaleFixture.cs`

**Interfaces:**
- Produces: `ZooScaleFixture` with `Task InitializeAsync()` (start container, migrate, seed ~50/2k/20k, rebuild the reverse index, build all authorizers), `Task DisposeAsync()`, and properties exposing each authorizer variant and the canonical probe inputs (`TenantContext`, a probe `SubjectRef`, a probe `EntityRef`, `Permission`, a fresh `RequestContext`). The seed uses a fixed-seed `Random` so the dataset is identical across runs.
- Consumes: `MigrationRunner`, `NpgsqlUnitOfWorkFactory`, `NpgsqlRelationStore`/`SchemaStore`/`AttributeStore`, `NpgsqlCteAuthorizer`, `IndexedAuthorizer`, `PostgresIndexStore` + the `m2/02` rebuild entry point, `PostgresCacheStore`, `EngineDrivenAuthorizer`, `InMemory*` stores, `CachingAuthorizer`.

> **One container, one seed, every variant.** Spinning Postgres and seeding 20k tuples is expensive; do it once in the fixture and share across both benchmark classes. The in-memory oracle is seeded from the *same model object* so all paths answer identically. The reverse index is populated by the `m2/02` full rebuild so the `IndexedAuthorizer` measures real indexed scans, not the fallback path.

> **Schema shape.** Reuse the worked-example schema (`group.member`, `animal` with `medicator`/`enclosure`/`species`/`blocked` and `permission edit = medicator + enclosure->edit + species->edit - blocked`, plus an `is_creator` conditioned branch) so Check exercises union/arrow/exclusion/condition and ListObjects exercises the type universe and conditioned re-check.

- [ ] **Step 1: Write the fixture**

```csharp
// tests/Relkit.Benchmarks/ZooScaleFixture.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Caching;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Relkit.Storage.Postgres;
using Testcontainers.PostgreSql;

namespace Relkit.Benchmarks;

/// <summary>
/// Seeds one Postgres container with the representative zoo dataset (~50 users, ~2k objects,
/// ~20k tuples) and builds every authorizer variant over it: in-memory oracle, CTE, and indexed —
/// each optionally wrapped in the cross-request cache. Deterministic seed for stable numbers.
/// </summary>
public sealed class ZooScaleFixture
{
    private const string Store = "zoo";
    public TenantContext Tenant { get; } = new(Store, "main");

    private PostgreSqlContainer _container = null!;
    public string ConnectionString { get; private set; } = "";

    // Authorizer variants (built in InitializeAsync).
    public EngineDrivenAuthorizer Oracle { get; private set; } = null!;
    public NpgsqlCteAuthorizer Cte { get; private set; } = null!;
    public IndexedAuthorizer Indexed { get; private set; } = null!;
    public CachingAuthorizer CteCached { get; private set; } = null!;
    public CachingAuthorizer IndexedCached { get; private set; } = null!;

    // Canonical probes (a subject with broad-ish access and a single object it can edit).
    public SubjectRef ProbeSubject { get; } = new("user", "user-1");
    public EntityRef ProbeObject { get; } = new("animal", "animal-1");
    public string Permission => "edit";
    public RequestContext Context => new(DateTimeOffset.UnixEpoch, ProbeSubject, new Dictionary<string, object?>());

    private static Schema BuildSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("enclosure", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor")))
        .Type("species", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor")))
        .Type("animal", t => t
            .Relation("medicator", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("enclosure", s => s.Type("enclosure"))
            .Relation("species", s => s.Type("species"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p
                .Relation("medicator")
                .Arrow("enclosure", "edit")
                .Arrow("species", "edit")
                .Exclude(x => x.Relation("blocked"))))
        .Condition("is_creator", c => { })
        .Build();

    // Builds the representative tuple set deterministically. ~50 users, ~2k animals, ~20k tuples.
    private static IReadOnlyList<RelationTuple> BuildTuples()
    {
        var rng = new Random(20260623);
        var tuples = new List<RelationTuple>();

        // Groups: 5 role groups, 3 team groups; teams nest into roles (two-level membership).
        var roles = Enumerable.Range(0, 5).Select(i => $"role-{i}").ToArray();
        var teams = Enumerable.Range(0, 3).Select(i => $"team-{i}").ToArray();
        foreach (var team in teams)
            tuples.Add(new RelationTuple(new EntityRef("group", roles[rng.Next(roles.Length)]), "member",
                new SubjectRef("group", team, "member")));

        // 50 users, each in 1–2 groups (mix of roles and teams).
        for (var i = 0; i < 50; i++)
        {
            var user = $"user-{i}";
            var g1 = rng.Next(2) == 0 ? roles[rng.Next(roles.Length)] : teams[rng.Next(teams.Length)];
            tuples.Add(new RelationTuple(new EntityRef("group", g1), "member", new SubjectRef("user", user)));
            if (rng.Next(2) == 0)
                tuples.Add(new RelationTuple(new EntityRef("group", teams[rng.Next(teams.Length)]), "member",
                    new SubjectRef("user", user)));
        }

        // ~20 enclosures and ~20 species, each editable by a role group.
        for (var i = 0; i < 20; i++)
        {
            tuples.Add(new RelationTuple(new EntityRef("enclosure", $"enc-{i}"), "editor",
                new SubjectRef("group", roles[i % roles.Length], "member")));
            tuples.Add(new RelationTuple(new EntityRef("species", $"sp-{i}"), "editor",
                new SubjectRef("group", roles[i % roles.Length], "member")));
        }

        // 2000 animals: each gets an enclosure edge, a species edge, and one of: a direct medicator
        // grant to a group, a wildcard grant, or a conditioned grant — plus a thin slice of `blocked`.
        for (var i = 0; i < 2000; i++)
        {
            var animal = new EntityRef("animal", $"animal-{i}");
            tuples.Add(new RelationTuple(animal, "enclosure", new SubjectRef("enclosure", $"enc-{i % 20}")));
            tuples.Add(new RelationTuple(animal, "species", new SubjectRef("species", $"sp-{i % 20}")));

            switch (i % 10)
            {
                case 0:   // public wildcard grant
                    tuples.Add(new RelationTuple(animal, "medicator", new SubjectRef("user", "*")));
                    break;
                case 1:   // conditioned grant (re-checked by ListObjects on the index path)
                    tuples.Add(new RelationTuple(animal, "medicator", new SubjectRef("group", roles[i % roles.Length], "member"),
                        new ConditionRef("is_creator", new Dictionary<string, object?>())));
                    break;
                default:  // direct group grant
                    tuples.Add(new RelationTuple(animal, "medicator", new SubjectRef("group", roles[i % roles.Length], "member")));
                    break;
            }

            if (i % 25 == 0)   // a slice of revocations exercising exclusion
                tuples.Add(new RelationTuple(animal, "blocked", new SubjectRef("user", $"user-{i % 50}")));
        }

        return tuples;
    }

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using (var conn = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await MigrationRunner.ApplyAsync(conn);
        }

        var schema = BuildSchema();
        var tuples = BuildTuples();
        var conditions = new NullConditionEvaluator();

        // ── Seed Postgres ──────────────────────────────────────────────────────────────────────
        var factory = new NpgsqlUnitOfWorkFactory(ConnectionString);
        var pgSchema = new NpgsqlSchemaStore(ConnectionString);
        var pgRelations = new NpgsqlRelationStore(ConnectionString);
        var pgAttributes = new NpgsqlAttributeStore(ConnectionString);
        await using (var conn = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(conn, "INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = Store });
            await Dapper.SqlMapper.ExecuteAsync(conn, "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
                new { s = Store, t = Tenant.Tenant });
        }
        await using (var u = await factory.BeginAsync())
        {
            await pgSchema.SetActiveAsync(Store, schema, u);
            await pgRelations.WriteAsync(Tenant, tuples, [], u);
            await u.CommitAsync();
        }

        // Populate the reverse index via the m2/02 full rebuild so the indexed path measures real scans.
        await ReverseIndexRebuild.RunAsync(ConnectionString, Tenant, schema.Version);

        // ── Seed the in-memory oracle from the same model ────────────────────────────────────────
        var memSchema = new InMemorySchemaStore();
        var memRelations = new InMemoryRelationStore();
        var memAttributes = new InMemoryAttributeStore();
        var memUow = new InMemoryUnitOfWork();
        await memSchema.SetActiveAsync(Store, schema, memUow);
        await memRelations.WriteAsync(Tenant, tuples, [], memUow);
        await memUow.CommitAsync();

        // ── Build every authorizer variant ───────────────────────────────────────────────────────
        Oracle = new EngineDrivenAuthorizer(memSchema, memRelations, memAttributes, conditions);
        Cte = new NpgsqlCteAuthorizer(ConnectionString, pgSchema, pgAttributes, conditions);
        var index = new PostgresIndexStore(ConnectionString);
        Indexed = new IndexedAuthorizer(Cte, index, pgSchema);

        var cacheFactory = new PostgresCacheStoreFactory(ConnectionString);
        CteCached = new CachingAuthorizer(Cte, pgSchema, cacheFactory.For(Tenant));
        // Wrapping IndexedAuthorizer would need the cacheability seam; the cross-request cache wraps the
        // CTE path for Check, and the indexed path for ListObjects is measured uncached vs the CTE cache.
        IndexedCached = new CachingAuthorizer(Cte, pgSchema, cacheFactory.For(Tenant));

        await SanityCheckAsync();
    }

    // Cheap fixture guard: all three paths agree on the probe. NOT the correctness mechanism (m1/08, m2/06).
    private async Task SanityCheckAsync()
    {
        var req = new CheckRequest(Tenant, ProbeObject, Permission, ProbeSubject, Context);
        var a = (await Oracle.CheckAsync(req)).Allowed;
        var b = (await Cte.CheckAsync(req)).Allowed;
        var c = (await Indexed.CheckAsync(req)).Allowed;
        if (a != b || b != c)
            throw new InvalidOperationException($"Fixture mismatch: oracle={a}, cte={b}, indexed={c}. Seed/index is misconfigured.");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
```

> **`ReverseIndexRebuild.RunAsync` and `PostgresIndexStore`** are the `m2/02`/`m2/01` entry points: the full rebuild that populates `reverse_index` for `(tenant, schema_version)`, and the `IIndexStore` the `IndexedAuthorizer` scans. If `m2/02` names the rebuild differently, adjust the one call here (reported as a Contract gap). `CachingAuthorizer` wraps `NpgsqlCteAuthorizer` because the cacheability seam lives on that concrete type (`m0/08`).

> **Wrapping `IndexedAuthorizer` in the cache.** `CachingAuthorizer` (`m0/08`) takes a concrete `NpgsqlCteAuthorizer` for the cacheability signal, so `IndexedCached` wraps the CTE path for Check; the indexed ListObjects path is benchmarked uncached against the CTE-cached Check baseline. This is the honest shape given the current `m0/08` seam; if a list-result cache lands later, add an indexed+cache ListObjects variant then.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build -c Release tests/Relkit.Benchmarks`
Expected: clean build (the fixture is not yet referenced by a benchmark class — that is Tasks 3–4).

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Benchmarks/ZooScaleFixture.cs
git commit -m "feat: add zoo-scale benchmark fixture seeding all authorizer variants"
```

---

### Task 3: `CheckBenchmarks` — Check latency across paths and cache

**Files:**
- Create: `tests/Relkit.Benchmarks/CheckBenchmarks.cs`

**Interfaces:**
- Produces: `CheckBenchmarks` with a `[Params]`-driven matrix over `Path` (Oracle / Cte / Index) and `Cached` (false / true), a `[GlobalSetup]` building the `ZooScaleFixture` once, a `[GlobalCleanup]` disposing it, and a `[Benchmark] Task<bool> Check()` running one `CheckAsync` on the canonical probe. The chosen authorizer is selected from the param matrix in `[GlobalSetup]`.
- Consumes: `ZooScaleFixture`, BenchmarkDotNet attributes.

> **Why one probe.** A single `(object, permission, subject)` makes every variant do equivalent work, so the numbers compare paths, not inputs. The probe subject reaches `edit` through group membership + an arrow into the enclosure, exercising the algebra. The cache param shows the warm-cache speedup; BenchmarkDotNet's warmup iterations populate the cache before measured runs, so `Cached=true` measures the hit path.

- [ ] **Step 1: Write the benchmark class**

```csharp
// tests/Relkit.Benchmarks/CheckBenchmarks.cs
using BenchmarkDotNet.Attributes;
using Relkit.Abstractions;

namespace Relkit.Benchmarks;

[MemoryDiagnoser]
public class CheckBenchmarks
{
    public enum Path { Oracle, Cte, Index }

    [Params(Path.Oracle, Path.Cte, Path.Index)]
    public Path ExecPath { get; set; }

    [Params(false, true)]
    public bool Cached { get; set; }

    private ZooScaleFixture _fx = null!;
    private IAuthorizer _auth = null!;
    private CheckRequest _request = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _fx = new ZooScaleFixture();
        await _fx.InitializeAsync();
        _auth = Select(_fx, ExecPath, Cached);
        _request = new CheckRequest(_fx.Tenant, _fx.ProbeObject, _fx.Permission, _fx.ProbeSubject, _fx.Context);
    }

    // The oracle is in-memory and has no cross-request cache; Cached=true on Oracle measures the same
    // path (BenchmarkDotNet still records it, so the grid stays rectangular and obviously comparable).
    private static IAuthorizer Select(ZooScaleFixture fx, Path path, bool cached) => (path, cached) switch
    {
        (Path.Oracle, _)      => fx.Oracle,
        (Path.Cte, false)     => fx.Cte,
        (Path.Cte, true)      => fx.CteCached,
        (Path.Index, false)   => fx.Indexed,
        (Path.Index, true)    => fx.IndexedCached,
        _                     => fx.Cte
    };

    [Benchmark]
    public async Task<bool> Check() => (await _auth.CheckAsync(_request)).Allowed;

    [GlobalCleanup]
    public async Task Cleanup() => await _fx.DisposeAsync();
}
```

- [ ] **Step 2: Build and verify the benchmark is discoverable**

Run: `dotnet run -c Release --project tests/Relkit.Benchmarks -- --list flat --filter *CheckBenchmarks*`
Expected: lists `Relkit.Benchmarks.CheckBenchmarks.Check`.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Benchmarks/CheckBenchmarks.cs
git commit -m "feat: add Check latency benchmark across paths and cache"
```

---

### Task 4: `ListObjectsBenchmarks` — ListObjects latency across paths and cache

**Files:**
- Create: `tests/Relkit.Benchmarks/ListObjectsBenchmarks.cs`

**Interfaces:**
- Produces: `ListObjectsBenchmarks` mirroring `CheckBenchmarks`: a `[Params]` matrix over `Path` and `Cached`, one `ZooScaleFixture` per `[GlobalSetup]`, and a `[Benchmark] Task<int> ListObjects()` returning the count of objects the probe subject may `edit` (a full first page at the default page size). This is the operation the reverse index most accelerates (spec §7.3), so the index vs CTE vs oracle spread is the headline result.
- Consumes: `ZooScaleFixture`, BenchmarkDotNet attributes.

> **Why count, not materialize.** Returning the page count keeps the benchmark body trivial and forces full enumeration of one page (so the work is real) without allocating a large result the harness would otherwise measure as noise. A fixed `PageSize` (100) makes the over-fetch/refill path do representative work on every variant.

- [ ] **Step 1: Write the benchmark class**

```csharp
// tests/Relkit.Benchmarks/ListObjectsBenchmarks.cs
using BenchmarkDotNet.Attributes;
using Relkit.Abstractions;

namespace Relkit.Benchmarks;

[MemoryDiagnoser]
public class ListObjectsBenchmarks
{
    public enum Path { Oracle, Cte, Index }

    [Params(Path.Oracle, Path.Cte, Path.Index)]
    public Path ExecPath { get; set; }

    [Params(false, true)]
    public bool Cached { get; set; }

    private ZooScaleFixture _fx = null!;
    private IAuthorizer _auth = null!;
    private ListObjectsRequest _request = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _fx = new ZooScaleFixture();
        await _fx.InitializeAsync();
        _auth = Select(_fx, ExecPath, Cached);
        _request = new ListObjectsRequest(
            _fx.Tenant, _fx.ProbeSubject, "animal", _fx.Permission, _fx.Context, PageSize: 100);
    }

    private static IAuthorizer Select(ZooScaleFixture fx, Path path, bool cached) => (path, cached) switch
    {
        (Path.Oracle, _)    => fx.Oracle,
        (Path.Cte, false)   => fx.Cte,
        (Path.Cte, true)    => fx.CteCached,
        (Path.Index, false) => fx.Indexed,
        (Path.Index, true)  => fx.IndexedCached,
        _                   => fx.Cte
    };

    [Benchmark]
    public async Task<int> ListObjects() => (await _auth.ListObjectsAsync(_request)).ObjectIds.Count;

    [GlobalCleanup]
    public async Task Cleanup() => await _fx.DisposeAsync();
}
```

- [ ] **Step 2: Build and verify the benchmark is discoverable**

Run: `dotnet run -c Release --project tests/Relkit.Benchmarks -- --list flat --filter *ListObjectsBenchmarks*`
Expected: lists `Relkit.Benchmarks.ListObjectsBenchmarks.ListObjects`.

- [ ] **Step 3: Run the full ListObjects benchmark end to end** (requires Docker for Testcontainers)

Run: `dotnet run -c Release --project tests/Relkit.Benchmarks -- --filter *ListObjectsBenchmarks*`
Expected: BenchmarkDotNet completes and prints a summary table with a row per `(ExecPath, Cached)`; the Index rows should be materially faster than the Cte rows for ListObjects at this scale. (Absolute numbers are environment-specific; the relative ordering is the result.)

- [ ] **Step 4: Commit**

```bash
git add tests/Relkit.Benchmarks/ListObjectsBenchmarks.cs
git commit -m "feat: add ListObjects latency benchmark across paths and cache"
```

---

### Task 5: Document how to run and read the benchmarks

**Files:**
- Create: `tests/Relkit.Benchmarks/README.md`

**Interfaces:**
- Produces: a short README documenting prerequisites (Docker for Testcontainers, Release build), the run commands, the parameter matrix, and how to interpret the output. This satisfies the "document how to run it" requirement.

- [ ] **Step 1: Write the README**

```markdown
<!-- tests/Relkit.Benchmarks/README.md -->
# Relkit Benchmarks

BenchmarkDotNet latency benchmarks for **Check** and **ListObjects** at representative zoo scale
(~50 users, ~2,000 objects, ~20,000 tuples), comparing the three execution paths — engine-driven
**oracle**, Postgres **CTE**, and index-backed **IndexedAuthorizer** — with and without the
cross-request cache.

## Prerequisites

- .NET 10 SDK.
- **Docker** running locally: the benchmarks seed a real Postgres via Testcontainers.
- A **Release** build (BenchmarkDotNet refuses to run a Debug build).

## Run

All benchmarks:

```bash
dotnet run -c Release --project tests/Relkit.Benchmarks -- --filter *
```

Just Check, or just ListObjects:

```bash
dotnet run -c Release --project tests/Relkit.Benchmarks -- --filter *CheckBenchmarks*
dotnet run -c Release --project tests/Relkit.Benchmarks -- --filter *ListObjectsBenchmarks*
```

List the available benchmarks without running:

```bash
dotnet run -c Release --project tests/Relkit.Benchmarks -- --list flat
```

## Parameter matrix

Each benchmark runs across two parameters:

| Parameter | Values | Meaning |
|---|---|---|
| `ExecPath` | `Oracle`, `Cte`, `Index` | engine-driven oracle / recursive-CTE path / reverse-index path |
| `Cached` | `false`, `true` | bypass vs use the cross-request `CachingAuthorizer` (CTE path) |

The container starts and seeds once per benchmark process (`[GlobalSetup]`); a fixture sanity check
asserts all three paths agree on the probe before any measurement.

## Reading the output

BenchmarkDotNet prints a summary table with a row per `(ExecPath, Cached)` combination, reporting
mean latency, allocation (`[MemoryDiagnoser]`), and statistical spread.

- **ListObjects** is where the reverse index pays off: `Index` rows should be materially faster than
  `Cte` rows at this scale (one indexed scan + conditioned re-check vs full candidate generation).
- **Check** with `Cached=true` shows the warm-cache hit path; warmup iterations populate the cache
  before measured runs.
- Absolute numbers are environment-specific (CPU, Docker IO); the **relative ordering** of the paths
  is the durable result. Re-run on the target host to get representative figures.
```

- [ ] **Step 2: Commit**

```bash
git add tests/Relkit.Benchmarks/README.md
git commit -m "docs: document how to run and read the Relkit benchmarks"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build -c Release tests/Relkit.Benchmarks` clean with `TreatWarningsAsErrors=true`.
- [ ] The fixture seeds ~50 users, ~2k objects, ~20k tuples deterministically and rebuilds the reverse index (Task 2).
- [ ] The fixture builds oracle / CTE / indexed authorizers plus cache-wrapped variants, and a sanity check asserts the three paths agree on the probe (Task 2).
- [ ] `CheckBenchmarks` and `ListObjectsBenchmarks` each run the `(ExecPath, Cached)` matrix over one canonical probe (Tasks 3–4).
- [ ] A full `--filter *ListObjectsBenchmarks*` run completes and prints a summary table (Task 4).
- [ ] The README documents prerequisites (Docker, Release), run commands, the parameter matrix, and how to read the output (Task 5).

## Contract gaps (reported, not changed)

- **Reverse-index rebuild entry point referenced as `ReverseIndexRebuild.RunAsync(connectionString, tenant, schemaVersion)`.** The fixture calls the `m2/02` full-rebuild to populate `reverse_index`. `m2/02` owns the exact type/method name; if it differs, adjust the single call in `ZooScaleFixture.InitializeAsync`. No `README.md` change made.
- **`IndexedAuthorizer`/`PostgresIndexStore` consumed from `m2/04`/`m2/01`.** The benchmark depends on these being present; they are M2 deliverables. No new contract surface is introduced by the benchmarks.
- **No cache variant wraps `IndexedAuthorizer` directly.** `CachingAuthorizer` (`m0/08`) wraps the concrete `NpgsqlCteAuthorizer` for the cacheability seam, so the cached variants wrap the CTE path. A dedicated list-result cache over the indexed path would need a new seam; out of this plan's scope and noted for a future caching plan. No `README.md` change made.
