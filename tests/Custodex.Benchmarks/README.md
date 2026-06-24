# Custodex Benchmarks

BenchmarkDotNet latency benchmarks for **Check** and **ListObjects** at representative scale
(~50 users, ~2,000 objects, ~20,000 tuples), comparing the three execution paths — the engine-driven
**oracle**, the Postgres **CTE** path, and the index-backed **IndexedAuthorizer**.

## Prerequisites

- .NET 10 SDK.
- **Docker** running locally: the benchmarks seed a real Postgres via Testcontainers.
- A **Release** build (BenchmarkDotNet requires an optimized build).

## Run

All benchmarks:

```bash
dotnet run -c Release --project tests/Custodex.Benchmarks -- --filter *
```

Just Check, or just ListObjects:

```bash
dotnet run -c Release --project tests/Custodex.Benchmarks -- --filter *CheckBenchmarks*
dotnet run -c Release --project tests/Custodex.Benchmarks -- --filter *ListObjectsBenchmarks*
```

List the available benchmarks without running them:

```bash
dotnet run -c Release --project tests/Custodex.Benchmarks -- --list flat
```

## Parameter matrix

Each benchmark runs across one parameter:

| Parameter | Values | Meaning |
|---|---|---|
| `ExecPath` | `Oracle`, `Cte`, `Index` | engine-driven oracle / recursive-CTE path / reverse-index path |

The container starts and seeds once per `ExecPath` value (`[GlobalSetup]`); a fixture sanity check
asserts all three paths agree on the probe before any measurement runs.

## Reading the output

BenchmarkDotNet prints a summary table with a row per `ExecPath`, reporting mean latency, allocation
(`[MemoryDiagnoser]`), and statistical spread.

- **ListObjects** is where the reverse index pays off: the `Index` rows are materially faster than the
  `Cte` rows at this scale, since the index serves a maintained scan rather than generating candidates
  from scratch.
- **Check** measures a single point decision through each path.
- Absolute numbers are environment-specific (CPU, Docker IO); the **relative ordering** of the paths is
  the durable result. Re-run on the target host for representative figures.
