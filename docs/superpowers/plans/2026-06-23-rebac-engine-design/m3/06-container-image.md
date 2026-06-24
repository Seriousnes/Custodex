# M3/06 — Container Image & Compose Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Package `Custodex.Service` as a container image, run it with Postgres via Docker Compose, expose health checks, and prove the running container answers a `Check`.

**Architecture:** A multi-stage Dockerfile builds and publishes `Custodex.Service` on the `net10.0` runtime image. Compose wires the service to a Postgres container; the service applies migrations on startup and exposes liveness/readiness endpoints. A smoke test brings the stack up and calls the API.

**Tech Stack:** .NET 10 SDK/runtime images, Docker Compose, ASP.NET health checks, xUnit, Testcontainers (for the compose smoke test).

## Global Constraints

See `../README.md` → Global Constraints. Depends on: `m3/01` (`Custodex.Service` host), `m3/02` (REST `/check`), `m3/03` (auth — the smoke test uses a seeded API key), `m1/01` (`MigrationRunner`), `m3/07` (ServiceDefaults wiring).

> **Aspire alignment** (see `../README.md` → Aspire integration): this plan targets the **production image and CI smoke test**. Local development orchestration (Postgres + Service) is the Aspire **AppHost** (`m3/07`), not docker-compose. Health endpoints come from ServiceDefaults' `MapDefaultEndpoints()` (`/health`, `/alive`) — Task 1 below registers the `PostgresHealthCheck` as a check **tagged for readiness** consumed by `/health`, rather than hand-mapping a separate `/health/ready`. Treat the `/health/live`,`/health/ready` paths in Task 1 as `/alive`,`/health` if you adopt the ServiceDefaults convention.

---

### Task 1: Health checks in the service

**Files:**
- Modify: `src/Custodex.Service/Program.cs`
- Create: `src/Custodex.Service/Health/PostgresHealthCheck.cs`
- Test: `tests/Custodex.Service.Tests/Health/HealthEndpointTests.cs`

**Interfaces:**
- Produces: `/health/live` (process up) and `/health/ready` (Postgres reachable + migrations applied).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Service.Tests/Health/HealthEndpointTests.cs
using System.Net;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests.Health;

public class HealthEndpointTests(CustodexServiceFactory factory) : IClassFixture<CustodexServiceFactory>
{
    [Fact]
    public async Task Liveness_is_ok()
    {
        var resp = await factory.CreateClient().GetAsync("/health/live");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_is_ok_when_db_is_up()
    {
        var resp = await factory.CreateClient().GetAsync("/health/ready");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Service.Tests --filter HealthEndpointTests`
Expected: FAIL — endpoints not mapped.

- [ ] **Step 3: Implement the health check and endpoints**

```csharp
// src/Custodex.Service/Health/PostgresHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Custodex.Service.Health;

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1 FROM schema_migrations LIMIT 1");
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres not ready", ex);
        }
    }
}
```

```csharp
// Program.cs additions
builder.Services.AddHealthChecks()
    .AddCheck<Custodex.Service.Health.PostgresHealthCheck>("postgres", tags: ["ready"]);

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });          // liveness: process only
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Service.Tests --filter HealthEndpointTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests/Health
git commit -m "feat: add liveness and readiness health checks"
```

---

### Task 2: Apply migrations on startup

**Files:**
- Modify: `src/Custodex.Service/Program.cs`
- Test: `tests/Custodex.Service.Tests/Startup/MigrationOnStartupTests.cs`

**Interfaces:**
- Consumes: `MigrationRunner` (m1/01).
- Produces: startup code that runs migrations before the app serves traffic (guarded by a `Custodex:ApplyMigrationsOnStartup` flag, default true).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Service.Tests/Startup/MigrationOnStartupTests.cs
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests.Startup;

public class MigrationOnStartupTests(CustodexServiceFactory factory) : IClassFixture<CustodexServiceFactory>
{
    [Fact]
    public async Task Tables_exist_after_startup()
    {
        // factory points at a fresh Testcontainers Postgres with no schema pre-applied
        var ready = await factory.CreateClient().GetAsync("/health/ready");
        ready.IsSuccessStatusCode.ShouldBeTrue();   // ready implies schema_migrations queryable
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Service.Tests --filter MigrationOnStartupTests`
Expected: FAIL — fresh DB has no tables; readiness fails.

- [ ] **Step 3: Run migrations at startup**

```csharp
// Program.cs, after building `app` and before `app.Run()`
if (builder.Configuration.GetValue("Custodex:ApplyMigrationsOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    await runner.ApplyAsync();
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Service.Tests --filter MigrationOnStartupTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests/Startup
git commit -m "feat: apply migrations on service startup"
```

---

### Task 3: The Dockerfile

**Files:**
- Create: `src/Custodex.Service/Dockerfile`
- Create: `.dockerignore` (repo root)

**Interfaces:**
- Produces: a publishable image exposing port 8080.

- [ ] **Step 1: Write the `.dockerignore`**

```
**/bin/
**/obj/
**/.vs/
**/.git/
**/*.user
tests/
docs/
```

- [ ] **Step 2: Write the multi-stage Dockerfile**

```dockerfile
# src/Custodex.Service/Dockerfile  (build context = repo root)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Custodex.Abstractions/ src/Custodex.Abstractions/
COPY src/Custodex.Core/ src/Custodex.Core/
COPY src/Custodex.Storage.Postgres/ src/Custodex.Storage.Postgres/
COPY src/Custodex.Service/ src/Custodex.Service/
RUN dotnet publish src/Custodex.Service/Custodex.Service.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Custodex.Service.dll"]
```

- [ ] **Step 3: Build the image to verify it compiles**

Run: `docker build -f src/Custodex.Service/Dockerfile -t Custodex-service:dev .`
Expected: build succeeds; final line `naming to docker.io/library/Custodex-service:dev`.

- [ ] **Step 4: Commit**

```bash
git add src/Custodex.Service/Dockerfile .dockerignore
git commit -m "build: add Custodex.Service Dockerfile"
```

---

### Task 4: Docker Compose with Postgres

**Files:**
- Create: `docker-compose.yml` (repo root)

**Interfaces:**
- Produces: a `db` (Postgres 17) + `Custodex` service stack; the service waits for Postgres health before starting.

- [ ] **Step 1: Write the compose file**

```yaml
# docker-compose.yml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_USER: Custodex
      POSTGRES_PASSWORD: Custodex
      POSTGRES_DB: Custodex
    ports: ["5432:5432"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U Custodex"]
      interval: 2s
      timeout: 3s
      retries: 20

  Custodex:
    build:
      context: .
      dockerfile: src/Custodex.Service/Dockerfile
    depends_on:
      db:
        condition: service_healthy
    environment:
      Custodex__ConnectionString: "Host=db;Username=Custodex;Password=Custodex;Database=Custodex"
      Custodex__ApiKeys__admin-key__Store: "zoo"
      Custodex__ApiKeys__admin-key__Role: "admin"
    ports: ["8080:8080"]
    healthcheck:
      test: ["CMD-SHELL", "wget -qO- http://localhost:8080/health/ready || exit 1"]
      interval: 3s
      timeout: 3s
      retries: 20
```

- [ ] **Step 2: Bring the stack up and verify readiness**

Run:
```bash
docker compose up -d --build
# wait for health, then:
curl -fsS http://localhost:8080/health/ready
```
Expected: `Healthy` (HTTP 200).

- [ ] **Step 3: Tear down**

Run: `docker compose down -v`

- [ ] **Step 4: Commit**

```bash
git add docker-compose.yml
git commit -m "build: add docker-compose with Postgres"
```

---

### Task 5: Compose smoke test

**Files:**
- Create: `tests/Custodex.Service.Tests/Container/ComposeSmokeTests.cs`

**Interfaces:**
- Consumes: the built image + compose; uses Testcontainers' compose/ambient support, or shells out to `docker compose`. The test is tagged `[Trait("category","container")]` so it can be excluded from the fast suite.

- [ ] **Step 1: Write the smoke test**

```csharp
// tests/Custodex.Service.Tests/Container/ComposeSmokeTests.cs
using System.Net.Http.Json;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests.Container;

[Trait("category", "container")]
public class ComposeSmokeTests
{
    [Fact]
    public async Task Running_container_answers_a_check()
    {
        // Assumes `docker compose up -d --build` has been run (or use a compose fixture).
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };
        http.DefaultRequestHeaders.Add("X-Custodex-Key", "admin-key");
        http.DefaultRequestHeaders.Add("X-Custodex-Tenant", "sydney-zoo");

        // seed a direct grant, then check it
        await http.PostAsJsonAsync("/tuples", SampleWrite.GrantCarolManageEl001);
        var resp = await http.PostAsJsonAsync("/check", SampleCheck.CarolManageEl001);
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();
        result!.Allowed.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run the container smoke test**

Run:
```bash
docker compose up -d --build
dotnet test tests/Custodex.Service.Tests --filter "category=container"
docker compose down -v
```
Expected: PASS — the containerized service grants `carol` manage on `EL-001`.

- [ ] **Step 3: Commit**

```bash
git add tests/Custodex.Service.Tests/Container
git commit -m "test: add compose smoke test against the running container"
```

---

## Self-review checklist (run after all tasks)

- [ ] Image builds from a clean checkout with build context = repo root.
- [ ] Service waits for Postgres health and applies migrations before serving.
- [ ] `/health/live` and `/health/ready` behave distinctly (readiness fails when the DB is down).
- [ ] The container smoke test is excluded from the fast unit suite via the `container` trait.
