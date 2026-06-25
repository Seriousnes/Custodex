# M3/06 — Container Image & Compose

**Goal:** Package `Custodex.Service` as a container image, run it with Postgres via Docker Compose, expose health checks, and prove the running container answers a `Check`.

**For implementers:** drive this plan with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each task is TDD where a test exists — Red → Green → Commit — tracked by its `- [ ]` checkbox. One Conventional Commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture:** a multi-stage Dockerfile builds and publishes `Custodex.Service` on the `net10.0` runtime image. Compose wires the service to a Postgres container; the service applies migrations on startup and exposes liveness/readiness. A smoke test brings the stack up and calls the API.

**Tech stack:** .NET 10 SDK/runtime images, Docker Compose, ASP.NET health checks, xUnit, Testcontainers (for the compose smoke test).

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies (see README):** the `Custodex.Service` host (`m3/01`), the REST `/v1/check` (`m3/02`), auth (`m3/03` — the smoke test uses a seeded API key), `MigrationRunner`, and the ServiceDefaults wiring (`m3/07`).

> **Aspire alignment** (see `../README.md` → Aspire integration): this plan targets the **production image and CI smoke test**. Local development orchestration (Postgres + Service) is the Aspire **AppHost** (`m3/07`), not docker-compose. Health flows through ServiceDefaults' `MapDefaultEndpoints()` — `/health` (all checks, including the readiness-tagged Postgres check) and `/alive` (live only). Register the `PostgresHealthCheck` tagged `"ready"` for consumption by `/health`; do not hand-map a separate `/health/ready`. Container/compose health probes hit `/health` and `/alive`.

---

### Task 1: Postgres readiness health check in the service

- [ ] **Files:** add `src/Custodex.Service/Health/PostgresHealthCheck.cs`; register it in `Program.cs` tagged `"ready"`; test `…Tests/Health/HealthEndpointTests.cs`.

**Produces:** an `IHealthCheck` that probes Postgres reachability + applied migrations, registered into the ServiceDefaults pipeline so `/health` reflects readiness and `/alive` reflects liveness (process up).
**Consumes (see README):** `NpgsqlDataSource`; `MapDefaultEndpoints()` from ServiceDefaults; ASP.NET health checks.

**Behavior:** the check runs a trivial query against the migrations table (`SELECT 1 FROM schema_migrations LIMIT 1`) and returns Healthy on success, Unhealthy (with the exception) otherwise — so readiness is false until Postgres is up and migrated. The check is tagged `"ready"` so `/health` includes it while `/alive` excludes it.

**Cases to pin:**

| Setup | Expect |
|---|---|
| GET `/alive` (process up) | 200 |
| GET `/health` with Postgres up + migrated | 200, names the `postgres` check |

**Done when:** build clean; cases pass; liveness and readiness behave distinctly.

---

### Task 2: Apply migrations on startup

- [ ] **Files:** modify `Program.cs`; test `…Tests/Startup/MigrationOnStartupTests.cs` over a fresh container with no pre-applied schema.

**Produces:** startup code that runs migrations before the app serves traffic, guarded by `Custodex:ApplyMigrationsOnStartup` (default true).
**Consumes (see README):** `MigrationRunner`.

**Behavior:** migrations run from a startup scope before `app.Run()`; because they are idempotent, a fresh container becomes ready purely from host startup. Readiness implies the migrations table is queryable. (This is the same obligation `m3/01` Task 7 states; if already in place, this task only adds the fresh-container readiness test.)

**Cases to pin:**

| Setup | Expect |
|---|---|
| boot against a fresh Postgres, GET `/health` | 200 (readiness implies `schema_migrations` queryable) |

**Done when:** build clean; the case passes.

---

### Task 3: The Dockerfile

- [ ] **Files:** add `src/Custodex.Service/Dockerfile` (build context = repo root); add `.dockerignore` at the repo root.

**Produces:** a publishable image exposing port 8080.
**Consumes (see README):** the engine source projects.

**Behavior:** a multi-stage build copies `Directory.Build.props` and the needed `src/` projects, publishes `Custodex.Service` in Release (framework-dependent), then copies the published output onto the runtime image.

| stage | base image | purpose |
|---|---|---|
| `build` | `dotnet/sdk:10.0` | copy `Directory.Build.props` + the `Abstractions`/`Core`/`Storage.Postgres`/`Service` projects; `dotnet publish -c Release` |
| `runtime` | `dotnet/aspnet:10.0` | copy the published app; `ASPNETCORE_HTTP_PORTS=8080`; `EXPOSE 8080`; entrypoint `dotnet Custodex.Service.dll` |

`.dockerignore` excludes `bin/`, `obj/`, `.vs/`, `.git/`, `*.user`, `tests/`, `docs/`.

**Done when:** `docker build -f src/Custodex.Service/Dockerfile .` succeeds from a clean checkout with the repo root as context.

---

### Task 4: Docker Compose with Postgres

- [ ] **Files:** add `docker-compose.yml` at the repo root.

**Produces:** a `db` (Postgres) + `Custodex` service stack; the service waits for Postgres health before starting.

**Behavior:**

| service | purpose |
|---|---|
| `db` | Postgres with the `Custodex` user/db; `pg_isready` healthcheck |
| `Custodex` | built from the Task-3 Dockerfile; `depends_on: db condition: service_healthy`; connection string + an admin API key (`Store=zoo`, `Role=admin`) via `Custodex__*` env vars; port 8080; healthcheck hits `/health` |

The service applies migrations on startup, so a fresh stack is ready once both healthchecks pass.

**Done when:** `docker compose up -d --build` brings the stack to health and `/health` returns 200; `docker compose down -v` tears it down.

---

### Task 5: Compose smoke test

- [ ] **Files:** add `…Tests/Container/ComposeSmokeTests.cs`, traited `[Trait("category","container")]` so it is excluded from the fast suite.

**Produces:** a smoke test that, against the running container, seeds a direct grant via REST and checks it.
**Consumes (see README):** the built image + compose; the REST `/v1/tuples` and `/v1/check` (`m3/02`); the seeded admin key + tenant header (`m3/03`).

**Behavior:** the test assumes the stack is up (`docker compose up -d --build`), sends the `X-Custodex-Key` admin key and `X-Custodex-Tenant` header, writes a grant, then checks it returns `allowed:true`. It is isolated behind the `container` trait so the unit suite never requires Docker.

**Cases to pin:**

| Setup | Expect |
|---|---|
| against the running container, write a direct grant then check it | `allowed:true` |

**Done when:** the traited smoke test passes against the running compose stack and is excluded from the fast suite.

---

## Self-review checklist

- [ ] Image builds from a clean checkout with build context = repo root.
- [ ] Service waits for Postgres health and applies migrations before serving.
- [ ] `/health` (incl. the readiness-tagged Postgres check) and `/alive` behave distinctly (readiness fails when the DB is down).
- [ ] The container smoke test is excluded from the fast unit suite via the `container` trait.
