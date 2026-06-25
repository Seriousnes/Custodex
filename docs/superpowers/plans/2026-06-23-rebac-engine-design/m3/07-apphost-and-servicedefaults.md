# M3/07 — AppHost & ServiceDefaults Integration

**Goal:** Wire `Custodex.Service` into the existing Aspire `Custodex.ServiceDefaults` (OpenTelemetry, health, discovery) — including the `"Custodex"` source/meter and a Postgres readiness check — and orchestrate Postgres + the service for local development through `Custodex.AppHost`.

**For implementers:** drive this plan with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each task is TDD where a test exists — Red → Green → Commit — tracked by its `- [ ]` checkbox. One Conventional Commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture:** `Custodex.ServiceDefaults` already provides `AddServiceDefaults()` (OTel traces/metrics/logs + OTLP export, `/health` + `/alive`, service discovery, HTTP resilience) and `MapDefaultEndpoints()`. This plan plugs the library's own telemetry (the `"Custodex"` `ActivitySource` + `Meter`) and a database readiness check into that pipeline, and adds the `AppHost` that provisions Postgres and runs the service against it — replacing docker-compose *for development* (the production image stays in `m3/06`).

**Tech stack:** .NET 10, .NET Aspire (`Aspire.Hosting.AppHost`, `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Testing`), OpenTelemetry, ASP.NET health checks, xUnit + Shouldly.

**Global Constraints:** see `../README.md` → Global Constraints and → Aspire integration. The `Custodex.Service`, `Custodex.AppHost`, and `Custodex.ServiceDefaults` projects **already exist** — do not scaffold them.

**Dependencies (see README):** `AddCustodex().UsePostgres().UseSchema()` and `AddCustodexInstrumentation()`; `CustodexDiagnostics` (the `"Custodex"` source/meter); `MigrationRunner`; the `Custodex.Service` host (`m3/01`). The Aspire integration tests require Docker.

---

### Task 1: Wire ServiceDefaults + Custodex telemetry + Postgres readiness into the service

- [ ] **Files:** modify `src/Custodex.Service/Program.cs`; add `src/Custodex.Service/Health/PostgresReadyHealthCheck.cs`; test `…Tests/Defaults/ServiceDefaultsWiringTests.cs`.

**Produces:** a `Custodex.Service` host whose `/health` includes a `"postgres"` readiness check and whose OTel pipeline exports the `"Custodex"` source/meter.
**Consumes (see README):** `AddServiceDefaults()`/`MapDefaultEndpoints()`; `AddCustodex().UsePostgres().UseSchema()` and `AddCustodexInstrumentation()`; `CustodexDiagnostics`.

**Behavior:** the host calls `AddServiceDefaults()` (no parallel OTel pipeline) and `MapDefaultEndpoints()`. The library telemetry registers into that pipeline via `AddCustodexInstrumentation()` — equivalently `tracing.AddSource("Custodex")` + `metrics.AddMeter("Custodex")` — called once, not both. The readiness check probes Postgres + applied migrations (the same `SELECT 1 FROM schema_migrations LIMIT 1` shape as `m3/06`) and is registered tagged `"ready"` so `/health` includes it and `/alive` does not.

**Cases to pin:**

| Setup | Expect |
|---|---|
| GET `/health` | 200, names the `postgres` check |
| GET `/alive` | 200 |

**Done when:** build clean; cases pass; the `"Custodex"` source/meter register exactly once and `/health` includes the readiness check.

---

### Task 2: AppHost orchestration — Postgres + Service

- [ ] **Files:** modify `src/Custodex.AppHost/AppHost.cs`; add the `Aspire.Hosting.PostgreSQL` package to `src/Custodex.AppHost/Custodex.AppHost.csproj`.

**Produces:** a distributed application graph — a Postgres resource with a `Custodex` database, and `Custodex.Service` referencing it and waiting for it.
**Consumes (see README):** `Aspire.Hosting.PostgreSQL`; the generated `Projects.Custodex_Service` reference.

**Behavior:** the AppHost adds a Postgres resource (with a data volume to persist across dev runs) and a `Custodex` database, then adds the service project with `WithReference(postgres)` (injecting `ConnectionStrings:Custodex`) and `WaitFor(postgres)`. This is the local-dev orchestration that replaces docker-compose.

**Done when:** `dotnet build src/Custodex.AppHost` succeeds and `Projects.Custodex_Service` resolves from the project reference.

---

### Task 3: Aspire integration smoke test

- [ ] **Files:** add `tests/Custodex.Storage.Postgres.Tests/Aspire/AppHostSmokeTests.cs`, traited `[Trait("category","aspire")]` (requires Docker; excluded from the fast suite); add `Aspire.Hosting.Testing` to that test project.

**Produces:** a smoke test that starts the real `Custodex.AppHost`, waits for the service to be healthy, then seeds a grant and checks it.
**Consumes (see README):** `Aspire.Hosting.Testing.DistributedApplicationTestingBuilder` over `Projects.Custodex_AppHost`; the REST `/v1/tuples` + `/v1/check`; the seeded admin key + tenant header.

**Behavior:** the test builds and starts the AppHost, obtains the service's HTTP client, waits for the resource to report healthy, then (with the admin key + tenant header) writes a direct grant and checks it returns `allowed:true` — proving the orchestrated stack (Postgres + migrated service) answers end-to-end. Behind the `aspire` trait so the unit suite never requires Docker.

**Cases to pin:**

| Setup | Expect |
|---|---|
| AppHost-orchestrated service, write a grant then check it | `allowed:true` |

**Done when:** the traited smoke test passes with Docker available and is excluded from the fast suite.

---

## Self-review checklist

- [ ] `Custodex.Service` calls `AddServiceDefaults()` and `MapDefaultEndpoints()`; no parallel OTel pipeline is created.
- [ ] The `"Custodex"` `ActivitySource` and `Meter` register into the ServiceDefaults OTel pipeline exactly once.
- [ ] `/health` includes the `postgres` readiness check; `/alive` reflects only live-tagged checks.
- [ ] The AppHost provisions Postgres and the service waits for it; the Aspire smoke test is excluded from the fast unit suite via the `aspire` trait.
