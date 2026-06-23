# M3/07 — AppHost & ServiceDefaults Integration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire `Relkit.Service` into the existing Aspire `Relkit.ServiceDefaults` (OpenTelemetry, health, discovery) including the `"Relkit"` source/meter and a Postgres readiness check, and orchestrate Postgres + the service for local development through `Relkit.AppHost`.

**Architecture:** `Relkit.ServiceDefaults` already provides `AddServiceDefaults()` (OTel traces/metrics/logs + OTLP export, `/health` + `/alive`, service discovery, HTTP resilience) and `MapDefaultEndpoints()`. This plan plugs the library's own telemetry and a database readiness check into that pipeline, and adds the `AppHost` that provisions Postgres and runs the service against it. This replaces docker-compose for local development; the production image stays in `m3/06`.

**Tech Stack:** .NET 10, .NET Aspire (`Aspire.Hosting.AppHost`, `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Testing`), OpenTelemetry, ASP.NET health checks, xUnit, Shouldly.

## Global Constraints

See `../README.md` → Global Constraints and → Aspire integration. The `Relkit.Service`, `Relkit.AppHost`, and `Relkit.ServiceDefaults` projects **already exist** (Aspire scaffold); do not `dotnet new` them. Depends on `m1/09` (`AddRelkit().UsePostgres().UseSchema()`, `AddRelkitInstrumentation()`), `m0/01` (`RelkitDiagnostics`), `m1/01` (`MigrationRunner`), `m3/01` (the `Relkit.Service` host). The Aspire integration tests require Docker.

---

### Task 1: Wire ServiceDefaults + Relkit telemetry + Postgres readiness into Relkit.Service

**Files:**
- Modify: `src/Relkit.Service/Program.cs`
- Create: `src/Relkit.Service/Health/PostgresReadyHealthCheck.cs`
- Test: `tests/Relkit.Service.Tests/Defaults/ServiceDefaultsWiringTests.cs`

**Interfaces:**
- Consumes: `AddServiceDefaults`/`MapDefaultEndpoints` (ServiceDefaults), `AddRelkit().UsePostgres().UseSchema()` and `AddRelkitInstrumentation()` (m1/09), `RelkitDiagnostics` (m0/01).
- Produces: a `Relkit.Service` host whose `/health` includes a `"postgres"` readiness check and whose OTel pipeline exports the `"Relkit"` source/meter.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Service.Tests/Defaults/ServiceDefaultsWiringTests.cs
using System.Net;
using Shouldly;
using Xunit;

namespace Relkit.Service.Tests.Defaults;

public class ServiceDefaultsWiringTests(RelkitServiceFactory factory) : IClassFixture<RelkitServiceFactory>
{
    [Fact]
    public async Task Health_endpoint_reports_postgres_check()
    {
        var resp = await factory.CreateClient().GetAsync("/health");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("postgres");
    }

    [Fact]
    public async Task Alive_endpoint_is_ok()
    {
        var resp = await factory.CreateClient().GetAsync("/alive");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Service.Tests --filter ServiceDefaultsWiringTests`
Expected: FAIL — endpoints/checks not wired.

- [ ] **Step 3: Implement the readiness check**

```csharp
// src/Relkit.Service/Health/PostgresReadyHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Relkit.Service.Health;

public sealed class PostgresReadyHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
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

- [ ] **Step 4: Wire the host** (`Program.cs`)

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();                              // OTel + /health,/alive + discovery + resilience

builder.AddRelkit()
    .UsePostgres(builder.Configuration.GetConnectionString("relkit")!)
    .UseSchema(ZooSchema.Build());                          // the app's schema builder

// register the library's telemetry into the ServiceDefaults OTel pipeline
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(Relkit.Abstractions.RelkitDiagnostics.Name))
    .WithMetrics(m => m.AddMeter(Relkit.Abstractions.RelkitDiagnostics.Name));

builder.Services.AddHealthChecks()
    .AddCheck<Relkit.Service.Health.PostgresReadyHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

app.MapDefaultEndpoints();                                  // /health (all) + /alive (live-tagged)
// ... gRPC (m3/01) + REST (m3/02) + auth (m3/03) mapping ...
app.Run();
```

> `AddRelkitInstrumentation()` (m1/09) MAY encapsulate the `AddSource`/`AddMeter` calls; if so, call it here instead of the inline `AddOpenTelemetry()` block. Keep one or the other, not both.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Service.Tests --filter ServiceDefaultsWiringTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Service tests/Relkit.Service.Tests/Defaults
git commit -m "feat: wire ServiceDefaults, Relkit telemetry, and Postgres readiness"
```

---

### Task 2: AppHost orchestration — Postgres + Service

**Files:**
- Modify: `src/Relkit.AppHost/AppHost.cs`
- Modify: `src/Relkit.AppHost/Relkit.AppHost.csproj` (add `Aspire.Hosting.PostgreSQL`)

**Interfaces:**
- Produces: a distributed application graph: a Postgres resource with a `relkit` database, and `Relkit.Service` referencing it and waiting for it.

- [ ] **Step 1: Add the Postgres hosting package**

Run:
```bash
dotnet add src/Relkit.AppHost package Aspire.Hosting.PostgreSQL
```

- [ ] **Step 2: Write the app graph**

```csharp
// src/Relkit.AppHost/AppHost.cs
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()                 // persist across dev runs
    .AddDatabase("relkit");

builder.AddProject<Projects.Relkit_Service>("relkit-service")
    .WithReference(postgres)          // injects ConnectionStrings:relkit
    .WaitFor(postgres);

builder.Build().Run();
```

- [ ] **Step 3: Verify the AppHost builds**

Run: `dotnet build src/Relkit.AppHost`
Expected: build succeeds; `Projects.Relkit_Service` resolves (generated from the project reference).

- [ ] **Step 4: Commit**

```bash
git add src/Relkit.AppHost
git commit -m "feat: orchestrate Postgres and Relkit.Service in the AppHost"
```

---

### Task 3: Aspire integration smoke test

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Aspire/AppHostSmokeTests.cs`
- Modify: `tests/Relkit.Storage.Postgres.Tests/Relkit.Storage.Postgres.Tests.csproj` (add `Aspire.Hosting.Testing`)

**Interfaces:**
- Consumes: `Aspire.Hosting.Testing.DistributedApplicationTestingBuilder` to start the real `Relkit.AppHost`.

- [ ] **Step 1: Add the testing package**

Run:
```bash
dotnet add tests/Relkit.Storage.Postgres.Tests package Aspire.Hosting.Testing
```

- [ ] **Step 2: Write the smoke test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Aspire/AppHostSmokeTests.cs
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Aspire;

[Trait("category", "aspire")]   // requires Docker; excluded from the fast suite
public class AppHostSmokeTests
{
    [Fact]
    public async Task Orchestrated_service_answers_a_check()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Relkit_AppHost>();
        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        var http = app.CreateHttpClient("relkit-service");
        await app.ResourceNotifications.WaitForResourceHealthyAsync("relkit-service");

        // direct grant then check (admin key seeded via AppHost config)
        http.DefaultRequestHeaders.Add("X-Relkit-Key", "admin-key");
        http.DefaultRequestHeaders.Add("X-Relkit-Tenant", "sydney-zoo");
        await http.PostAsJsonAsync("/v1/tuples", SampleWrite.GrantCarolManageEl001);
        var resp = await http.PostAsJsonAsync("/v1/check", SampleCheck.CarolManageEl001);
        var result = await resp.Content.ReadFromJsonAsync<CheckResponseDto>();
        result!.Allowed.ShouldBeTrue();
    }
}
```

- [ ] **Step 3: Run the Aspire smoke test** (Docker required)

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter "category=aspire"`
Expected: PASS — the AppHost starts Postgres + the service, applies migrations, and the orchestrated service grants `carol` manage on `EL-001`.

- [ ] **Step 4: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Aspire
git commit -m "test: Aspire AppHost smoke test for the orchestrated service"
```

---

## Self-review checklist (run after all tasks)

- [ ] `Relkit.Service` calls `AddServiceDefaults()` and `MapDefaultEndpoints()`; no parallel OTel pipeline is created.
- [ ] The `"Relkit"` ActivitySource and Meter are registered into the ServiceDefaults OTel pipeline exactly once.
- [ ] `/health` includes the `postgres` readiness check; `/alive` reflects only live-tagged checks.
- [ ] The AppHost provisions Postgres and the service waits for it; the Aspire smoke test is excluded from the fast unit suite via the `aspire` trait.
