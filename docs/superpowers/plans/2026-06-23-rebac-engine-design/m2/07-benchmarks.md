# M2/07 — BenchmarkDotNet Suite

**Goal:** Stand up the `Custodex.Benchmarks` BenchmarkDotNet project measuring **Check** and **ListObjects** latency at representative zoo scale — a Testcontainers Postgres seeded with ~50 users, ~2k objects, ~20k tuples — comparing the three execution paths (engine-driven **oracle**, Postgres **CTE**, index-backed **IndexedAuthorizer**), plus the cross-request cache effect on the engine-driven path — the only authorizer that exposes the cacheability seam today — and document how to run and read it (spec §11.1, §11.4).

**For implementers:** drive this with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each `### Task` is one TDD/build unit, committed as one Conventional-Commit with the co-author trailer (see `../README.md` → Global Constraints). Tasks are tracked with `- [x]` checkboxes. (Benchmarks are measurement code, so the rhythm is build-and-verify-discoverable rather than Red→Green.)

**Architecture/approach:** one `Custodex.Benchmarks` console project under `tests/`. A shared `ZooScaleFixture` starts a single Postgres container, applies migrations, seeds the representative dataset once (deterministic seed for comparable runs), and builds every authorizer variant over it: the in-memory `EngineDrivenAuthorizer` oracle (seeded from the same model), the `NpgsqlCteAuthorizer`, and the `IndexedAuthorizer` over a `reverse_index` populated by the m2/02 full rebuild. The cache variant wraps the **engine-driven** authorizer in `CachingAuthorizer` (m0/08) over a `PostgresCacheStore` — the only path exposing the cacheability seam today (README → Post-dispatch reconciliations); the CTE and indexed production paths are measured uncached. BenchmarkDotNet `[GlobalSetup]` builds the fixture once per process; `[Benchmark]` methods run the hot operation. Two classes — `CheckBenchmarks` and `ListObjectsBenchmarks` — each parameterised by path (oracle/cte/index) and cache (on/off, meaningful only for the engine-driven path), measuring a fixed canonical probe so every variant does equivalent work.

**Tech stack:** .NET 10 (`net10.0`), C# 14, BenchmarkDotNet, Npgsql, Dapper, Testcontainers.PostgreSql. References `Custodex.Core`, `Custodex.Storage.Postgres`, `Custodex.Storage.InMemory`.

**Global Constraints:** see `../README.md` → Global Constraints. The benchmark seed uses a fixed-seed `Random` for reproducibility, never inside evaluation.

**Dependencies:** builds on m0/04 (`InMemory*` stores); m0/05/m0/07 (`EngineDrivenAuthorizer`); m0/08 (`CachingAuthorizer`, `CacheValueCodec`); m1/01 (`MigrationRunner`); m1/03/m1/04 (Npgsql stores + unit of work); m1/05/m1/06 (`NpgsqlCteAuthorizer`); m2/01/m2/02 (`NpgsqlIndexStore` + `ReverseIndexRebuilder.RebuildAsync`); m2/04 (`IndexedAuthorizer`); m2/05 (`PostgresCacheStore`, `PostgresCacheStoreFactory`).

> **Benchmarks are measurement, not correctness.** Equivalence of the three paths is proven by m1/08 and m2/06; this suite assumes that and measures latency. A tiny sanity assertion in `[GlobalSetup]` (all three authorizers agree on one probe) catches a misconfigured fixture early but is not the correctness mechanism.

## Representative scale (spec §9.2 / §11.1 target)

The seeded dataset targets the spec's per-tenant scale ("dozens to low-hundreds of users, low-thousands of resources, tens of thousands of tuples"):

- **~50 users**, members of a few groups (roles/teams) with nested membership two levels deep.
- **~2,000 objects** of type `animal`, each with an `enclosure` and a `species` structural edge.
- **~20,000 tuples** total: group memberships, per-object grants, structural edges, plus a slice of conditioned grants and a slice of `user:*` wildcard grants so every path exercises its conditioned-recheck and type-universe branches.

A single tenant under one store; the seed is deterministic so latency numbers are stable across runs.

---

### Task 1: Create the `Custodex.Benchmarks` project

- [x] **Files:** create `tests/Custodex.Benchmarks/Custodex.Benchmarks.csproj` and `…/Program.cs`; add to `Custodex.slnx`.

**Produces:** a runnable BenchmarkDotNet console app referencing the engine, the Postgres provider, and the in-memory provider, with a `Program` entry point dispatching to the benchmark classes via `BenchmarkSwitcher.FromAssembly(...).Run(args)`.
**Consumes (see README):** BenchmarkDotNet, Testcontainers.PostgreSql, Npgsql, Dapper; the three project references above.

**Behavior:** the project is `Exe`, not packable, server-GC on. BenchmarkDotNet refuses a non-optimized build, so it must run in Release; `TreatWarningsAsErrors` from `Directory.Build.props` still applies. The entry point is the standard `BenchmarkSwitcher` dispatch (filter by class name on the command line).

**Done when:** `dotnet run -c Release --project tests/Custodex.Benchmarks -- --list flat` builds clean and prints the (initially empty) benchmark list without error.

---

### Task 2: `ZooScaleFixture` — seed the dataset and build every authorizer

- [x] **Files:** create `tests/Custodex.Benchmarks/ZooScaleFixture.cs`.

**Produces:** `ZooScaleFixture` with `InitializeAsync` (start container, migrate, seed ~50/2k/20k, rebuild the reverse index, build all authorizers), `DisposeAsync`, and properties exposing each authorizer variant and the canonical probe inputs (`TenantContext`, a probe `SubjectRef`, a probe `EntityRef`, permission, a fresh `RequestContext`).
**Consumes (see README):** `MigrationRunner`, `NpgsqlUnitOfWorkFactory`, the Npgsql relation/schema/attribute stores, `NpgsqlCteAuthorizer`, `IndexedAuthorizer`, `NpgsqlIndexStore` + `ReverseIndexRebuilder.RebuildAsync`, `PostgresCacheStore`/`PostgresCacheStoreFactory`, `EngineDrivenAuthorizer`, the `InMemory*` stores, `CachingAuthorizer`.

**Behavior:** one container, one seed, every variant — spinning Postgres and seeding 20k tuples is expensive, so do it once and share across both benchmark classes. KEY DECISIONS:
- **Schema shape:** the worked-example schema (`group.member`; `animal` with `medicator`/`enclosure`/`species`/`blocked` and `edit = medicator + enclosure->edit + species->edit - blocked`, plus an `is_creator` conditioned branch), so Check exercises union/arrow/exclusion/condition and ListObjects exercises the type universe and conditioned re-check.
- **Deterministic seed composition** (fixed RNG seed): 5 role groups + 3 team groups with teams nesting into roles (two-level membership); 50 users each in 1–2 groups; ~20 enclosures and ~20 species each editable by a role group; 2,000 animals each with an enclosure edge, a species edge, and one grant chosen by `i % 10` — a `user:*` wildcard slice, a conditioned (`is_creator`) slice, and a direct group-grant majority — plus a thin `blocked` slice (`i % 25`) exercising exclusion. The proportions guarantee every path hits its wildcard / conditioned-recheck / exclusion branches.
- **The in-memory oracle is seeded from the same model object** so all paths answer identically; the reverse index is populated by the m2/02 full rebuild so the `IndexedAuthorizer` measures real indexed scans, not the fallback path.
- **Caching applies to the engine-driven path only:** `CachingAuthorizer` (m0/08) wraps the `ICacheableAuthorizer` seam, which only `EngineDrivenAuthorizer` implements; `NpgsqlCteAuthorizer` does not expose it (README → Post-dispatch reconciliations), so the CTE and indexed production paths are benchmarked uncached. The cached variant measures the cache hit-path cost on the engine-driven authorizer over a `PostgresCacheStore`.
- A cheap `[GlobalSetup]`-time sanity check asserts oracle/CTE/indexed agree on the probe and throws on mismatch (a misconfigured seed/index), but is explicitly **not** the correctness mechanism (m1/08, m2/06 are).

**Done when:** `dotnet build -c Release tests/Custodex.Benchmarks` is clean (the fixture is referenced by the benchmark classes in Tasks 3–4).

---

### Task 3: `CheckBenchmarks` — Check latency across paths and cache

- [x] **Files:** create `tests/Custodex.Benchmarks/CheckBenchmarks.cs`.

**Produces:** `CheckBenchmarks` with a `[Params]` matrix over path (Oracle/Cte/Index) and cached (false/true), a `[GlobalSetup]` building the fixture once, `[GlobalCleanup]` disposing it, and a `[Benchmark] Check()` running one `CheckAsync` on the canonical probe; `[MemoryDiagnoser]` on.
**Consumes (see README):** `ZooScaleFixture`, BenchmarkDotNet attributes.

**Behavior:** one probe makes every variant do equivalent work, so the numbers compare paths, not inputs — the probe subject reaches `edit` through group membership + an arrow into the enclosure, exercising the algebra. The cache param shows the warm-cache speedup (BenchmarkDotNet warmup iterations populate the cache before measured runs, so `Cached=true` measures the hit path). KEY DECISION: only `EngineDrivenAuthorizer` exposes the cacheability seam, so `Cached=true` is meaningful only for `Oracle`; the grid stays rectangular by mapping `(Cte,true)`/`(Index,true)` to the same uncached CTE/index authorizers (their cached row equals their uncached row), and the README (Task 5) records that the CTE/index production paths are uncached today.

**Done when:** `dotnet run -c Release --project tests/Custodex.Benchmarks -- --list flat --filter *CheckBenchmarks*` lists `CheckBenchmarks.Check`.

---

### Task 4: `ListObjectsBenchmarks` — ListObjects latency across paths and cache

- [x] **Files:** create `tests/Custodex.Benchmarks/ListObjectsBenchmarks.cs`.

**Produces:** `ListObjectsBenchmarks` mirroring `CheckBenchmarks`: the path × cached matrix, one fixture per `[GlobalSetup]`, and a `[Benchmark] ListObjects()` returning the count of objects the probe subject may `edit` (a full first page at the default page size); `[MemoryDiagnoser]` on.
**Consumes (see README):** `ZooScaleFixture`, BenchmarkDotNet attributes.

**Behavior:** ListObjects is the operation the reverse index most accelerates (spec §7.3), so the index vs CTE vs oracle spread is the headline result. KEY DECISION: return the **page count**, not a materialized list — this keeps the benchmark body trivial and forces full enumeration of one page (real work) without allocating a large result the harness would measure as noise. A fixed `PageSize` (100) makes the over-fetch/refill path do representative work on every variant.

**Done when:** `--list flat --filter *ListObjectsBenchmarks*` lists `ListObjectsBenchmarks.ListObjects`; a full `--filter *ListObjectsBenchmarks*` run (Docker required) completes and prints a summary row per `(path, cached)` — Index rows materially faster than Cte rows for ListObjects at this scale (relative ordering is the result; absolute numbers are environment-specific).

---

### Task 5: Document how to run and read the benchmarks

- [x] **Files:** create `tests/Custodex.Benchmarks/README.md`.

**Produces:** a short README for the benchmark project documenting prerequisites (Docker for Testcontainers, Release build — BenchmarkDotNet refuses Debug), the run commands (all / Check-only / ListObjects-only / `--list flat`), the `(ExecPath, Cached)` parameter matrix, and how to interpret the output (mean latency + allocation via `[MemoryDiagnoser]`; ListObjects is where the index pays off; Check `Cached=true` shows the warm-cache hit path on the engine-driven authorizer — the only cacheable path today; absolute numbers are environment-specific, relative ordering is the durable result). This satisfies the "document how to run it" requirement.

**Done when:** the README documents prerequisites, run commands, the parameter matrix, and how to read the output.

---

## Self-review checklist (after all tasks)

- [x] `dotnet build -c Release tests/Custodex.Benchmarks` clean under `TreatWarningsAsErrors=true`.
- [x] The fixture seeds ~50 users, ~2k objects, ~20k tuples deterministically and rebuilds the reverse index (Task 2).
- [x] The fixture builds oracle / CTE / indexed authorizers plus a cache-wrapped engine-driven variant, and a sanity check asserts the three paths agree on the probe (Task 2).
- [x] `CheckBenchmarks` and `ListObjectsBenchmarks` each run the `(ExecPath, Cached)` matrix over one canonical probe (Tasks 3–4).
- [x] A full `--filter *ListObjectsBenchmarks*` run completes and prints a summary table (Task 4).
- [x] The README documents prerequisites, run commands, the matrix, and how to read the output (Task 5).

## Contract gaps (reported, not changed)

- **Reverse-index rebuild + index store consumed from m2/02/m2/01 by their owners' names** (`ReverseIndexRebuilder.RebuildAsync`, `NpgsqlIndexStore`). The fixture depends on these M2 deliverables being present; no new contract surface is introduced by the benchmarks.
- **The cacheability seam (`ICacheableAuthorizer`) is implemented only by `EngineDrivenAuthorizer`.** `NpgsqlCteAuthorizer` does not expose it (README → Post-dispatch reconciliations), so the CTE and indexed production paths are benchmarked uncached and the cache variant measures the engine-driven path. Caching a production path would require adding the seam to the CTE authorizer. No README change by this plan.
