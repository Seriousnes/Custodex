# M3/01 — gRPC Contracts & Service Host Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up `Custodex.Service` — an ASP.NET host (`net10.0`) exposing the engine over gRPC. Define `.proto` contracts that mirror `IAuthorizer` (Check/BatchCheck/ListObjects/ListSubjects) and the management surface (`IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager`), and implement gRPC services that delegate to the **same** in-process `Custodex.Core` + Postgres engine wired by `AddCustodex().UsePostgres().UseSchema()` (spec §4, §10.1). The proto messages map faithfully to the canonical contract records (`EntityRef`/`SubjectRef`/`RequestContext`/`ExplainNode`/`ConditionRef.Parameters`).

**Architecture:** `Custodex.Service` references `Custodex.Extensions.DependencyInjection` and `Custodex.Storage.Postgres` and composes the engine exactly as the in-process Blazor consumer does (`m1/09`). The gRPC layer is a thin translation boundary: each gRPC service depends only on the public `Custodex.Abstractions` interfaces (`IAuthorizer`, `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager`) resolved from DI, converts proto messages to contract records, calls the interface, and converts the result back. A shared `ProtoMap` static class owns every record ⇄ message conversion so the mapping is defined once and tested in isolation. `RequestContext.Attributes` and `ConditionRef.Parameters` (both `IReadOnlyDictionary<string, object?>`) map to `google.protobuf.Struct` so heterogeneous attribute/parameter values round-trip without a bespoke variant type.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, ASP.NET Core, `Grpc.AspNetCore`, `Google.Protobuf`, `Grpc.Tools`, the engine packages (`Custodex.Abstractions`/`Custodex.Core`/`Custodex.Storage.Postgres`/`Custodex.Extensions.DependencyInjection`), xUnit, Shouldly, `Grpc.Net.Client`, `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`), `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. Key points: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 license metadata; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. **No EF Core.** Depends on `m0/01` (the `Custodex.Abstractions` contract — every type name below is verbatim from there), `m1/09` (`AddCustodex().UsePostgres(conn).UseSchema(builder)`, the concrete managers, `AddCustodexInstrumentation()`). The service composes the same `Custodex.Core` + Postgres engine behind a network front door; it adds **zero** new evaluation semantics.

> **Aspire alignment** (see `../README.md` → Aspire integration): `Custodex.Service` already exists and uses Aspire ServiceDefaults. Do not `dotnet new` the project. The host calls `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`; build the gRPC services on that existing host, and enable `AddGrpcClientInstrumentation()` in the ServiceDefaults tracing config.

## Shared decisions (locked)

- **The service is a thin front door.** gRPC services hold only the `Custodex.Abstractions` interfaces; all engine behaviour comes from `AddCustodex().UsePostgres().UseSchema()` (`m1/09`). No evaluation, validation, or persistence logic lives in `Custodex.Service`.
- **One `.proto` package, four service definitions.** `Custodex.v1` declares `Decision` (the four read ops), `Relations`, `Schema`, and `Provisioning` (the management surface). Splitting management from decisions lets `m3/03` apply distinct authorization policies per service.
- **Heterogeneous values use `google.protobuf.Struct`.** `RequestContext.Attributes` and `ConditionRef.Parameters` carry `object?` values (int/long/double/bool/string). `Struct`/`Value` round-trips them; `ProtoMap` centralises the `object? ⇄ Value` conversion (int/long → number, bool → bool, string → string, null → null value).
- **Tenancy is explicit in the proto for M3/01.** Every request message carries `store` + `tenant` fields that build a `TenantContext`. `m3/03` moves resolution to headers/claims; here the messages model `TenantContext` directly so the contract is complete and testable before auth lands.
- **Auth is deferred to `m3/03`.** This plan maps the host with `WebApplicationFactory` over a Testcontainers Postgres and leaves the endpoints open; `m3/03` adds the API-key/OIDC schemes and the management-vs-decision authorization policies.

---

### Task 1: Create the `Custodex.Service` host project and prove it boots

**Files:**
- Create: `src/Custodex.Service/Custodex.Service.csproj`
- Create: `src/Custodex.Service/Program.cs`
- Create: `src/Custodex.Service/appsettings.json`
- Create: `tests/Custodex.Service.Tests/Custodex.Service.Tests.csproj`
- Create: `tests/Custodex.Service.Tests/PostgresFixture.cs`
- Test: `tests/Custodex.Service.Tests/HostBootTests.cs`

**Interfaces:**
- Produces: a runnable ASP.NET host that calls `AddCustodex().UsePostgres(conn).UseSchema(builder)` and maps a health endpoint; a `WebApplicationFactory`-friendly `Program` (partial class exposed for tests).
- Consumes: `AddCustodex`/`UsePostgres`/`UseSchema`/`AddCustodexInstrumentation` (`m1/09`), `SchemaBuilder` (`m0/02`).

- [ ] **Step 1: Create the projects and references**

Run:
```bash
dotnet new web -n Custodex.Service -o src/Custodex.Service -f net10.0
dotnet new xunit -n Custodex.Service.Tests -o tests/Custodex.Service.Tests -f net10.0
rm tests/Custodex.Service.Tests/UnitTest1.cs
dotnet sln add src/Custodex.Service tests/Custodex.Service.Tests
dotnet add src/Custodex.Service reference src/Custodex.Abstractions
dotnet add src/Custodex.Service reference src/Custodex.Core
dotnet add src/Custodex.Service reference src/Custodex.Storage.Postgres
dotnet add src/Custodex.Service reference src/Custodex.Extensions.DependencyInjection
dotnet add src/Custodex.Service package Grpc.AspNetCore
dotnet add src/Custodex.Service package OpenTelemetry.Extensions.Hosting
dotnet add tests/Custodex.Service.Tests reference src/Custodex.Service
dotnet add tests/Custodex.Service.Tests reference src/Custodex.Abstractions
dotnet add tests/Custodex.Service.Tests reference src/Custodex.Core
dotnet add tests/Custodex.Service.Tests package Shouldly
dotnet add tests/Custodex.Service.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add tests/Custodex.Service.Tests package Grpc.Net.Client
dotnet add tests/Custodex.Service.Tests package Testcontainers.PostgreSql
```

- [ ] **Step 2: Write `appsettings.json`**

```json
// src/Custodex.Service/appsettings.json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Custodex": {
    "ConnectionString": "Host=localhost;Port=5432;Database=Custodex;Username=Custodex;Password=Custodex"
  }
}
```

- [ ] **Step 3: Write `Program.cs`** (health endpoint + engine wiring; gRPC services are mapped in later tasks)

```csharp
// src/Custodex.Service/Program.cs
using Custodex.Core;
using Custodex.Extensions.DependencyInjection;
using Custodex.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

var connectionString = builder.Configuration.GetValue<string>("Custodex:ConnectionString")
    ?? throw new InvalidOperationException("Custodex:ConnectionString is not configured.");

// The service composes the SAME Custodex.Core + Postgres engine the in-process consumer uses (spec §4).
// A startup schema can be supplied; absent one, schemas are managed at runtime via ISchemaManager.
builder.Services.AddCustodex().UsePostgres(connectionString);

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddCustodexInstrumentation())
    .WithMetrics(m => m.AddCustodexInstrumentation());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Exposed so WebApplicationFactory&lt;Program&gt; can host the service in tests.</summary>
public partial class Program;
```

- [ ] **Step 4: Write the Postgres fixture** (one container shared by the test collection)

```csharp
// tests/Custodex.Service.Tests/PostgresFixture.cs
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Custodex.Service.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        // The service's startup path runs MigrationRunner; for tests we also ensure the schema exists.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("service")]
public sealed class ServiceCollectionFixture : ICollectionFixture<PostgresFixture>;
```

> `MigrationRunner.ApplyAsync` is the idempotent migration entry point from `m1/01`. `Custodex.Service` runs it at startup (Task 7); the fixture also runs it so a `WebApplicationFactory` pointed at a freshly started container has the tables ready.

- [ ] **Step 5: Write the failing host-boot test**

```csharp
// tests/Custodex.Service.Tests/HostBootTests.cs
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests;

[Collection("service")]
public class HostBootTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Custodex:ConnectionString", fx.ConnectionString));

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("ok");
    }

    [Fact]
    public void Engine_is_wired_and_authorizer_resolves()
    {
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<Custodex.Abstractions.IAuthorizer>().ShouldNotBeNull();
    }
}
```

- [ ] **Step 6: Run to verify**

Run: `dotnet test tests/Custodex.Service.Tests --filter HostBootTests`
Expected: PASS (2 tests). The host boots, wires the engine over Testcontainers Postgres, and resolves `IAuthorizer`.

- [ ] **Step 7: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests
git commit -m "feat: scaffold Custodex.Service host wired to the Postgres engine"
```

---

### Task 2: The decision `.proto` and generated stubs

**Files:**
- Create: `src/Custodex.Service/Protos/Custodex_common.proto`
- Create: `src/Custodex.Service/Protos/Custodex_decision.proto`
- Modify: `src/Custodex.Service/Custodex.Service.csproj`
- Test: `tests/Custodex.Service.Tests/ProtoCompileTests.cs`

**Interfaces:**
- Produces: `Custodex.v1` proto package with the shared messages (`EntityRef`, `SubjectRef`, `ConditionRef`, `RequestContext`, `ExplainNode`) and the `Decision` service (`Check`, `BatchCheck`, `ListObjects`, `ListSubjects`) with their request/response messages. `Grpc.Tools` generates the C# server stubs at build.
- Consumes: `google/protobuf/struct.proto`, `google/protobuf/timestamp.proto` (well-known types ship with `Google.Protobuf`).

> **Faithful mapping.** Each proto message mirrors a contract record field-for-field: `EntityRef{type,id}` ⇄ `EntityRef(Type,Id)`; `SubjectRef{type,id,relation}` ⇄ `SubjectRef(Type,Id,Relation?)` (empty `relation` ⇒ `null`, i.e. not a subject-set); `ConditionRef{name,parameters}` ⇄ `ConditionRef(Name, Parameters)` with `parameters` a `Struct`; `RequestContext{now,subject,attributes}` ⇄ `RequestContext(Now,Subject,Attributes)`; `ExplainNode{description,allowed,children}` ⇄ `ExplainNode(Description,Allowed,Children)` (recursive).

- [ ] **Step 1: Write the common proto**

```proto
// src/Custodex.Service/Protos/Custodex_common.proto
syntax = "proto3";

package Custodex.v1;

import "google/protobuf/struct.proto";
import "google/protobuf/timestamp.proto";

option csharp_namespace = "Custodex.Service.Grpc";

// Mirrors Custodex.Abstractions.EntityRef. id "*" denotes a wildcard.
message EntityRef {
  string type = 1;
  string id = 2;
}

// Mirrors Custodex.Abstractions.SubjectRef. An empty relation means "not a subject-set".
message SubjectRef {
  string type = 1;
  string id = 2;
  string relation = 3; // empty => null Relation (e.g. user:alice); non-empty => subject-set (group:vets#member)
}

// Mirrors Custodex.Abstractions.ConditionRef. parameters carry heterogeneous values.
message ConditionRef {
  string name = 1;
  google.protobuf.Struct parameters = 2;
}

// Mirrors Custodex.Abstractions.RequestContext. attributes are ad-hoc request-context values.
message RequestContext {
  google.protobuf.Timestamp now = 1;
  SubjectRef subject = 2;
  google.protobuf.Struct attributes = 3;
}

// Mirrors Custodex.Abstractions.ExplainNode (recursive decision trace).
message ExplainNode {
  string description = 1;
  bool allowed = 2;
  repeated ExplainNode children = 3;
}

// Tenancy carried explicitly in M3/01; m3/03 resolves it from headers/claims instead.
message TenantContext {
  string store = 1;
  string tenant = 2;
}
```

- [ ] **Step 2: Write the decision proto**

```proto
// src/Custodex.Service/Protos/Custodex_decision.proto
syntax = "proto3";

package Custodex.v1;

import "Custodex_common.proto";

option csharp_namespace = "Custodex.Service.Grpc";

service Decision {
  rpc Check (CheckRequest) returns (CheckResponse);
  rpc BatchCheck (BatchCheckRequest) returns (BatchCheckResponse);
  rpc ListObjects (ListObjectsRequest) returns (ListObjectsResponse);
  rpc ListSubjects (ListSubjectsRequest) returns (ListSubjectsResponse);
}

message CheckRequest {
  TenantContext tenant = 1;
  EntityRef object = 2;
  string permission = 3;
  SubjectRef subject = 4;
  RequestContext context = 5;
  bool explain = 6;
}

message CheckResponse {
  bool allowed = 1;
  ExplainNode explain = 2; // present only when the request set explain = true
}

message CheckItem {
  EntityRef object = 1;
  string permission = 2;
  SubjectRef subject = 3;
}

message BatchCheckRequest {
  TenantContext tenant = 1;
  repeated CheckItem items = 2;
  RequestContext context = 3;
}

message BatchCheckResponse {
  repeated CheckResponse results = 1; // positionally aligned with the request items
}

message ListObjectsRequest {
  TenantContext tenant = 1;
  SubjectRef subject = 2;
  string object_type = 3;
  string permission = 4;
  RequestContext context = 5;
  int32 page_size = 6;
  string continuation_token = 7; // empty => first page
}

message ListObjectsResponse {
  repeated string object_ids = 1;
  string continuation_token = 2; // empty => no further pages
}

message ListSubjectsRequest {
  TenantContext tenant = 1;
  EntityRef object = 2;
  string permission = 3;
  RequestContext context = 4;
  int32 page_size = 5;
  string continuation_token = 6;
}

message ListSubjectsResponse {
  repeated SubjectRef subjects = 1;
  string continuation_token = 2;
}
```

- [ ] **Step 2b: Register the protos in the csproj**

Add to `src/Custodex.Service/Custodex.Service.csproj` (inside the `<Project>` element):

```xml
  <ItemGroup>
    <Protobuf Include="Protos\Custodex_common.proto" GrpcServices="None" />
    <Protobuf Include="Protos\Custodex_decision.proto" GrpcServices="Server" />
  </ItemGroup>
```

> `Custodex_common.proto` generates message types only (`GrpcServices="None"`); the service protos that import it generate the server base classes. `Grpc.AspNetCore` brings `Grpc.Tools` transitively, so no extra package is needed for generation.

- [ ] **Step 3: Write the failing proto-compile test**

```csharp
// tests/Custodex.Service.Tests/ProtoCompileTests.cs
using Custodex.Service.Grpc;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests;

public class ProtoCompileTests
{
    [Fact]
    public void Generated_decision_messages_exist()
    {
        var req = new CheckRequest
        {
            Tenant = new TenantContext { Store = "zoo", Tenant = "t1" },
            Object = new EntityRef { Type = "animal", Id = "EL-001" },
            Permission = "edit",
            Subject = new SubjectRef { Type = "user", Id = "alice" },
            Explain = true,
        };
        req.Permission.ShouldBe("edit");
        req.Object.Id.ShouldBe("EL-001");
    }

    [Fact]
    public void Decision_service_base_type_is_generated()
    {
        typeof(Decision.DecisionBase).ShouldNotBeNull();
    }
}
```

- [ ] **Step 4: Run to verify**

Run: `dotnet test tests/Custodex.Service.Tests --filter ProtoCompileTests`
Expected: PASS (2 tests). The protos compile and `Decision.DecisionBase` is generated. If generation fails, confirm the `<Protobuf>` items and that `Custodex_common.proto` uses `GrpcServices="None"`.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests
git commit -m "feat: add decision gRPC contracts and generated stubs"
```

---

### Task 3: `ProtoMap` — record ⇄ message conversion

**Files:**
- Create: `src/Custodex.Service/Mapping/ProtoMap.cs`
- Test: `tests/Custodex.Service.Tests/ProtoMapTests.cs`

**Interfaces:**
- Produces: `ProtoMap` static class with bidirectional converters for every shared type: `ToEntityRef`/`FromEntityRef`, `ToSubjectRef`/`FromSubjectRef`, `ToConditionRef`/`FromConditionRef`, `ToRequestContext`/`FromRequestContext`, `ToTenantContext`, `ToExplainNode` (record → message), and the `Struct ⇄ IReadOnlyDictionary<string, object?>` helpers `ToStruct`/`FromStruct`.
- Consumes: `EntityRef`, `SubjectRef`, `ConditionRef`, `RequestContext`, `TenantContext`, `ExplainNode` (`Custodex.Abstractions`); `Google.Protobuf.WellKnownTypes` (`Struct`, `Value`, `Timestamp`).

> **The `null` Relation rule is the subtle one.** `SubjectRef.Relation` is `null` for a plain subject and a non-null string for a subject-set; proto3 strings cannot be null, so an **empty** `relation` field maps to `null` and any non-empty value maps through. `IsSubjectSet` therefore stays correct across the wire.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Service.Tests/ProtoMapTests.cs
using Google.Protobuf.WellKnownTypes;
using Custodex.Abstractions;
using Custodex.Service.Mapping;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests;

public class ProtoMapTests
{
    [Fact]
    public void EntityRef_round_trips_including_wildcard()
    {
        var domain = new EntityRef("user", "*");
        var proto = ProtoMap.FromEntityRef(domain);
        proto.Type.ShouldBe("user");
        proto.Id.ShouldBe("*");
        ProtoMap.ToEntityRef(proto).ShouldBe(domain);
    }

    [Fact]
    public void SubjectRef_empty_relation_maps_to_null_and_subject_set_round_trips()
    {
        var plain = ProtoMap.ToSubjectRef(new Grpc.SubjectRef { Type = "user", Id = "alice", Relation = "" });
        plain.Relation.ShouldBeNull();
        plain.IsSubjectSet.ShouldBeFalse();

        var set = new SubjectRef("group", "vets", "member");
        var proto = ProtoMap.FromSubjectRef(set);
        proto.Relation.ShouldBe("member");
        ProtoMap.ToSubjectRef(proto).ShouldBe(set);
    }

    [Fact]
    public void Struct_round_trips_heterogeneous_attribute_values()
    {
        var attrs = new Dictionary<string, object?>
        {
            ["count"] = 30L,
            ["ratio"] = 1.5d,
            ["active"] = true,
            ["owner"] = "dr-smith",
            ["missing"] = null,
        };
        var s = ProtoMap.ToStruct(attrs);
        var back = ProtoMap.FromStruct(s);

        // Numbers come back as double from Struct; the condition evaluator coerces numerics.
        Convert.ToDouble(back["count"]).ShouldBe(30d);
        Convert.ToDouble(back["ratio"]).ShouldBe(1.5d);
        back["active"].ShouldBe(true);
        back["owner"].ShouldBe("dr-smith");
        back["missing"].ShouldBeNull();
    }

    [Fact]
    public void ConditionRef_round_trips_with_parameters()
    {
        var cond = new ConditionRef("within_hours",
            new Dictionary<string, object?> { ["start"] = 8L, ["end"] = 18L });
        var proto = ProtoMap.FromConditionRef(cond);
        proto.Name.ShouldBe("within_hours");
        var back = ProtoMap.ToConditionRef(proto)!;
        back.Name.ShouldBe("within_hours");
        Convert.ToInt32(back.Parameters["end"]).ShouldBe(18);
    }

    [Fact]
    public void RequestContext_maps_now_subject_and_attributes()
    {
        var ctx = new Grpc.RequestContext
        {
            Now = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch),
            Subject = new Grpc.SubjectRef { Type = "user", Id = "alice", Relation = "" },
            Attributes = ProtoMap.ToStruct(new Dictionary<string, object?> { ["ip"] = "10.0.0.1" }),
        };
        var domain = ProtoMap.ToRequestContext(ctx);
        domain.Now.ShouldBe(DateTimeOffset.UnixEpoch);
        domain.Subject.Id.ShouldBe("alice");
        domain.Attributes["ip"].ShouldBe("10.0.0.1");
    }

    [Fact]
    public void ExplainNode_maps_recursively()
    {
        var node = new ExplainNode("union", true,
        [
            new ExplainNode("relation medicator", true, []),
            new ExplainNode("arrow enclosure->edit", false, []),
        ]);
        var proto = ProtoMap.FromExplainNode(node);
        proto.Description.ShouldBe("union");
        proto.Allowed.ShouldBeTrue();
        proto.Children.Count.ShouldBe(2);
        proto.Children[1].Allowed.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Service.Tests --filter ProtoMapTests`
Expected: FAIL — `ProtoMap` does not exist.

- [ ] **Step 3: Implement `ProtoMap`**

```csharp
// src/Custodex.Service/Mapping/ProtoMap.cs
using Google.Protobuf.WellKnownTypes;
using Custodex.Abstractions;
using Grpc = Custodex.Service.Grpc;

namespace Custodex.Service.Mapping;

/// <summary>
/// Bidirectional conversion between the canonical Custodex.Abstractions records and the Custodex.v1
/// proto messages. The single home for the record ⇄ message mapping so the boundary is tested once.
/// </summary>
public static class ProtoMap
{
    public static EntityRef ToEntityRef(Grpc.EntityRef e) => new(e.Type, e.Id);
    public static Grpc.EntityRef FromEntityRef(EntityRef e) => new() { Type = e.Type, Id = e.Id };

    // Empty proto relation => null domain Relation (plain subject); non-empty => subject-set.
    public static SubjectRef ToSubjectRef(Grpc.SubjectRef s) =>
        new(s.Type, s.Id, string.IsNullOrEmpty(s.Relation) ? null : s.Relation);

    public static Grpc.SubjectRef FromSubjectRef(SubjectRef s) =>
        new() { Type = s.Type, Id = s.Id, Relation = s.Relation ?? "" };

    public static TenantContext ToTenantContext(Grpc.TenantContext t) => new(t.Store, t.Tenant);

    public static ConditionRef? ToConditionRef(Grpc.ConditionRef? c) =>
        c is null ? null : new ConditionRef(c.Name, FromStruct(c.Parameters));

    public static Grpc.ConditionRef FromConditionRef(ConditionRef c) =>
        new() { Name = c.Name, Parameters = ToStruct(c.Parameters) };

    public static RequestContext ToRequestContext(Grpc.RequestContext? c)
    {
        if (c is null)
            return new RequestContext(default, new SubjectRef("user", "*"), new Dictionary<string, object?>());
        return new RequestContext(
            c.Now?.ToDateTimeOffset() ?? default,
            c.Subject is null ? new SubjectRef("user", "*") : ToSubjectRef(c.Subject),
            FromStruct(c.Attributes));
    }

    public static Grpc.ExplainNode FromExplainNode(ExplainNode n)
    {
        var node = new Grpc.ExplainNode { Description = n.Description, Allowed = n.Allowed };
        foreach (var child in n.Children)
            node.Children.Add(FromExplainNode(child));
        return node;
    }

    public static Struct ToStruct(IReadOnlyDictionary<string, object?> values)
    {
        var s = new Struct();
        foreach (var (key, value) in values)
            s.Fields[key] = ToValue(value);
        return s;
    }

    public static IReadOnlyDictionary<string, object?> FromStruct(Struct? s)
    {
        var result = new Dictionary<string, object?>();
        if (s is null) return result;
        foreach (var (key, value) in s.Fields)
            result[key] = FromValue(value);
        return result;
    }

    private static Value ToValue(object? value) => value switch
    {
        null => Value.ForNull(),
        bool b => Value.ForBool(b),
        string str => Value.ForString(str),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
            => Value.ForNumber(Convert.ToDouble(value)),
        _ => Value.ForString(value.ToString() ?? ""),
    };

    private static object? FromValue(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.NullValue => null,
        Value.KindOneofCase.BoolValue => value.BoolValue,
        Value.KindOneofCase.StringValue => value.StringValue,
        Value.KindOneofCase.NumberValue => value.NumberValue,
        _ => null,
    };
}
```

> **Numeric fidelity note.** `Struct` has a single `number` kind (double). Integer attributes/parameters therefore arrive as `double`; the condition evaluator (`m0/06`) already coerces numerics for comparisons (`at_least`, `within_hours`), so this is faithful for the engine's purposes. The tests assert via `Convert.ToInt32`/`Convert.ToDouble` to document the coercion.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Service.Tests --filter ProtoMapTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests
git commit -m "feat: add ProtoMap record-message conversion"
```

---

### Task 4: `DecisionService` — the four read ops over gRPC

**Files:**
- Create: `src/Custodex.Service/Services/DecisionGrpcService.cs`
- Modify: `src/Custodex.Service/Program.cs`
- Test: `tests/Custodex.Service.Tests/DecisionServiceTests.cs`

**Interfaces:**
- Produces: `DecisionGrpcService : Decision.DecisionBase` delegating each RPC to `IAuthorizer`, translating via `ProtoMap`. Mapped at `/Custodex.v1.Decision` by `app.MapGrpcService<DecisionGrpcService>()`.
- Consumes: `IAuthorizer` (`Custodex.Abstractions`), `ProtoMap` (Task 3), the generated `Decision.DecisionBase` (Task 2).

> **Delegation only.** The service builds a `CheckRequest`/`ListObjectsRequest`/etc. from the proto message, calls the resolved `IAuthorizer`, and maps the result back. The same `IAuthorizer` (`NpgsqlCteAuthorizer`, registered by `m1/09`'s `UsePostgres`) serves both this front door and the in-process consumer.

- [ ] **Step 1: Implement the decision service**

```csharp
// src/Custodex.Service/Services/DecisionGrpcService.cs
using Grpc.Core;
using Custodex.Abstractions;
using Custodex.Service.Grpc;
using Custodex.Service.Mapping;

namespace Custodex.Service.Services;

/// <summary>gRPC front door for the four read operations. Delegates to the in-process IAuthorizer.</summary>
public sealed class DecisionGrpcService(IAuthorizer authorizer) : Decision.DecisionBase
{
    public override async Task<CheckResponse> Check(CheckRequest request, ServerCallContext context)
    {
        var result = await authorizer.CheckAsync(new Custodex.Abstractions.CheckRequest(
            ProtoMap.ToTenantContext(request.Tenant),
            ProtoMap.ToEntityRef(request.Object),
            request.Permission,
            ProtoMap.ToSubjectRef(request.Subject),
            ProtoMap.ToRequestContext(request.Context),
            request.Explain), context.CancellationToken);

        var response = new CheckResponse { Allowed = result.Allowed };
        if (result.Explain is not null)
            response.Explain = ProtoMap.FromExplainNode(result.Explain);
        return response;
    }

    public override async Task<BatchCheckResponse> BatchCheck(BatchCheckRequest request, ServerCallContext context)
    {
        var items = request.Items
            .Select(i => new CheckItem(ProtoMap.ToEntityRef(i.Object), i.Permission, ProtoMap.ToSubjectRef(i.Subject)))
            .ToList();

        var results = await authorizer.BatchCheckAsync(new Custodex.Abstractions.BatchCheckRequest(
            ProtoMap.ToTenantContext(request.Tenant), items,
            ProtoMap.ToRequestContext(request.Context)), context.CancellationToken);

        var response = new BatchCheckResponse();
        foreach (var r in results)
            response.Results.Add(new CheckResponse { Allowed = r.Allowed });
        return response;
    }

    public override async Task<ListObjectsResponse> ListObjects(ListObjectsRequest request, ServerCallContext context)
    {
        var result = await authorizer.ListObjectsAsync(new Custodex.Abstractions.ListObjectsRequest(
            ProtoMap.ToTenantContext(request.Tenant),
            ProtoMap.ToSubjectRef(request.Subject),
            request.ObjectType,
            request.Permission,
            ProtoMap.ToRequestContext(request.Context),
            request.PageSize <= 0 ? 100 : request.PageSize,
            string.IsNullOrEmpty(request.ContinuationToken) ? null : request.ContinuationToken),
            context.CancellationToken);

        var response = new ListObjectsResponse { ContinuationToken = result.ContinuationToken ?? "" };
        response.ObjectIds.AddRange(result.ObjectIds);
        return response;
    }

    public override async Task<ListSubjectsResponse> ListSubjects(ListSubjectsRequest request, ServerCallContext context)
    {
        var result = await authorizer.ListSubjectsAsync(new Custodex.Abstractions.ListSubjectsRequest(
            ProtoMap.ToTenantContext(request.Tenant),
            ProtoMap.ToEntityRef(request.Object),
            request.Permission,
            ProtoMap.ToRequestContext(request.Context),
            request.PageSize <= 0 ? 100 : request.PageSize,
            string.IsNullOrEmpty(request.ContinuationToken) ? null : request.ContinuationToken),
            context.CancellationToken);

        var response = new ListSubjectsResponse { ContinuationToken = result.ContinuationToken ?? "" };
        foreach (var s in result.Subjects)
            response.Subjects.Add(ProtoMap.FromSubjectRef(s));
        return response;
    }
}
```

- [ ] **Step 2: Map the service in `Program.cs`**

Add `app.MapGrpcService<DecisionGrpcService>();` before `app.Run();` and add the using:

```csharp
// src/Custodex.Service/Program.cs — add near the top
using Custodex.Service.Services;
```

```csharp
// src/Custodex.Service/Program.cs — add before app.Run();
app.MapGrpcService<DecisionGrpcService>();
```

- [ ] **Step 3: Write the failing end-to-end gRPC test**

```csharp
// tests/Custodex.Service.Tests/DecisionServiceTests.cs
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Grpc;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests;

[Collection("service")]
public class DecisionServiceTests(PostgresFixture fx)
{
    private const string Store = "grpc-dec";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Custodex:ConnectionString", fx.ConnectionString));

    private static Decision.DecisionClient ClientFor(WebApplicationFactory<Program> factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        });
        return new Decision.DecisionClient(channel);
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, string tenant)
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var stores = sp.GetRequiredService<IStoreManager>();
        var tenants = sp.GetRequiredService<ITenantManager>();
        var schemas = sp.GetRequiredService<ISchemaManager>();
        var relations = sp.GetRequiredService<IRelationManager>();

        await stores.CreateStoreAsync(Store);
        await tenants.CreateTenantAsync(new TenantContext(Store, tenant));
        await tenants.CreateTenantAsync(new TenantContext(Store, Store)); // schema-audit bookkeeping tenant

        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("species", t => t
                .Relation("editor", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("editor")))
            .Build();
        await schemas.SetActiveSchemaAsync(Store, schema);

        var t = new TenantContext(Store, tenant);
        await relations.WriteTuplesAsync(t, "zoo-admin",
        [
            new RelationTuple(new EntityRef("species", "kangaroo"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("species", "wallaby"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("group", "macropods"), "member", new SubjectRef("user", "alice")),
        ]);
    }

    private static CheckRequest CheckMsg(string tenant, string objectId, string subjectId) => new()
    {
        Tenant = new TenantContext { Store = Store, Tenant = tenant },
        Object = new EntityRef { Type = "species", Id = objectId },
        Permission = "edit",
        Subject = new SubjectRef { Type = "user", Id = subjectId },
        Context = new RequestContext { Subject = new SubjectRef { Type = "user", Id = subjectId } },
    };

    [Fact]
    public async Task Check_returns_allowed_for_a_granted_subject()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-check");
        var client = ClientFor(factory);

        var response = await client.CheckAsync(CheckMsg("t-check", "kangaroo", "alice"));

        response.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_returns_denied_for_an_ungranted_subject()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-deny");
        var client = ClientFor(factory);

        (await client.CheckAsync(CheckMsg("t-deny", "kangaroo", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ListObjects_returns_the_objects_a_subject_can_edit()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-list");
        var client = ClientFor(factory);

        var response = await client.ListObjectsAsync(new ListObjectsRequest
        {
            Tenant = new TenantContext { Store = Store, Tenant = "t-list" },
            Subject = new SubjectRef { Type = "user", Id = "alice" },
            ObjectType = "species",
            Permission = "edit",
            Context = new RequestContext { Subject = new SubjectRef { Type = "user", Id = "alice" } },
        });

        response.ObjectIds.OrderBy(x => x).ShouldBe(["kangaroo", "wallaby"]);
    }

    [Fact]
    public async Task BatchCheck_returns_results_aligned_with_items()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-batch");
        var client = ClientFor(factory);

        var request = new BatchCheckRequest
        {
            Tenant = new TenantContext { Store = Store, Tenant = "t-batch" },
            Context = new RequestContext { Subject = new SubjectRef { Type = "user", Id = "alice" } },
        };
        request.Items.Add(new CheckItem
        {
            Object = new EntityRef { Type = "species", Id = "kangaroo" }, Permission = "edit",
            Subject = new SubjectRef { Type = "user", Id = "alice" },
        });
        request.Items.Add(new CheckItem
        {
            Object = new EntityRef { Type = "species", Id = "kangaroo" }, Permission = "edit",
            Subject = new SubjectRef { Type = "user", Id = "bob" },
        });

        var response = await client.BatchCheckAsync(request);

        response.Results.Count.ShouldBe(2);
        response.Results[0].Allowed.ShouldBeTrue();
        response.Results[1].Allowed.ShouldBeFalse();
    }
}
```

- [ ] **Step 4: Run to verify**

Run: `dotnet test tests/Custodex.Service.Tests --filter DecisionServiceTests`
Expected: FAIL first (service unmapped), then PASS (4 tests) once Steps 1–2 are in place. The seeded macropods grant lets `alice` edit `kangaroo`/`wallaby` and denies `bob`.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests
git commit -m "feat: add DecisionService gRPC implementation over IAuthorizer"
```

---

### Task 5: The management `.proto` and generated stubs

**Files:**
- Create: `src/Custodex.Service/Protos/Custodex_management.proto`
- Modify: `src/Custodex.Service/Custodex.Service.csproj`
- Test: `tests/Custodex.Service.Tests/ManagementProtoCompileTests.cs`

**Interfaces:**
- Produces: the `Relations`, `Schema`, and `Provisioning` services in `Custodex.v1`, plus their messages: `RelationTuple`, tuple write/delete/read, attribute write, change-log read; schema validate/set/get (schema carried as a JSON string for M3/01, with the DSL parser deferred to `m3/05`); store/tenant create.
- Consumes: `Custodex_common.proto`.

> **Schema over the wire as JSON.** `ISchemaManager` takes the canonical `Schema` AST. For the gRPC surface the schema travels as a `schema_json` string (the canonical-model serialization); `m3/05`'s DSL parser adds a text form. M3/01 maps `schema_json ⇄ Schema` with `System.Text.Json` so the management API is complete now. `RelationTuple` mirrors the record: object + relation + subject + optional condition.

- [ ] **Step 1: Write the management proto**

```proto
// src/Custodex.Service/Protos/Custodex_management.proto
syntax = "proto3";

package Custodex.v1;

import "Custodex_common.proto";
import "google/protobuf/struct.proto";
import "google/protobuf/timestamp.proto";

option csharp_namespace = "Custodex.Service.Grpc";

// Mirrors Custodex.Abstractions.RelationTuple.
message RelationTuple {
  EntityRef object = 1;
  string relation = 2;
  SubjectRef subject = 3;
  ConditionRef condition = 4; // optional
}

service Relations {
  rpc WriteTuples (WriteTuplesRequest) returns (WriteTuplesResponse);
  rpc DeleteTuples (WriteTuplesRequest) returns (WriteTuplesResponse);
  rpc WriteAttributes (WriteAttributesRequest) returns (WriteAttributesResponse);
  rpc ReadTuples (ReadTuplesRequest) returns (ReadTuplesResponse);
  rpc ReadChangeLog (ReadChangeLogRequest) returns (ReadChangeLogResponse);
}

message WriteTuplesRequest {
  TenantContext tenant = 1;
  string actor = 2;
  repeated RelationTuple tuples = 3;
}
message WriteTuplesResponse { int32 count = 1; }

message WriteAttributesRequest {
  TenantContext tenant = 1;
  string actor = 2;
  EntityRef object = 3;
  google.protobuf.Struct attributes = 4;
}
message WriteAttributesResponse {}

// Mirrors Custodex.Abstractions.TupleFilter (all fields optional; empty => no filter on that column).
message ReadTuplesRequest {
  TenantContext tenant = 1;
  string object_type = 2;
  string object_id = 3;
  string relation = 4;
  string subject_type = 5;
  string subject_id = 6;
}
message ReadTuplesResponse { repeated RelationTuple tuples = 1; }

// Mirrors Custodex.Abstractions.ChangeLogFilter.
message ReadChangeLogRequest {
  TenantContext tenant = 1;
  google.protobuf.Timestamp since = 2; // optional
  string actor = 3;                    // optional
  int32 limit = 4;                     // <=0 => default 100
}
message ChangeLogEntry {
  int64 id = 1;
  string actor = 2;
  string operation = 3;
  string target = 4;
  string before_json = 5; // serialized before-image, empty if null
  string after_json = 6;  // serialized after-image, empty if null
  google.protobuf.Timestamp occurred_at = 7;
}
message ReadChangeLogResponse { repeated ChangeLogEntry entries = 1; }

service Schema {
  rpc Validate (ValidateSchemaRequest) returns (ValidateSchemaResponse);
  rpc SetActive (SetActiveSchemaRequest) returns (SetActiveSchemaResponse);
  rpc GetActive (GetActiveSchemaRequest) returns (GetActiveSchemaResponse);
}

message ValidateSchemaRequest { string schema_json = 1; }
message ValidateSchemaResponse {
  bool is_valid = 1;
  repeated string errors = 2;
}
message SetActiveSchemaRequest {
  string store = 1;
  string schema_json = 2;
}
message SetActiveSchemaResponse {}
message GetActiveSchemaRequest { string store = 1; }
message GetActiveSchemaResponse {
  bool found = 1;
  string schema_json = 2; // empty when found = false
}

service Provisioning {
  rpc CreateStore (CreateStoreRequest) returns (CreateStoreResponse);
  rpc CreateTenant (CreateTenantRequest) returns (CreateTenantResponse);
}
message CreateStoreRequest { string store = 1; }
message CreateStoreResponse {}
message CreateTenantRequest { TenantContext tenant = 1; }
message CreateTenantResponse {}
```

- [ ] **Step 2: Register the management proto in the csproj**

Add to the existing `<ItemGroup>` of protos in `src/Custodex.Service/Custodex.Service.csproj`:

```xml
    <Protobuf Include="Protos\Custodex_management.proto" GrpcServices="Server" />
```

- [ ] **Step 3: Write the failing compile test**

```csharp
// tests/Custodex.Service.Tests/ManagementProtoCompileTests.cs
using Custodex.Service.Grpc;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests;

public class ManagementProtoCompileTests
{
    [Fact]
    public void Management_service_base_types_are_generated()
    {
        typeof(Relations.RelationsBase).ShouldNotBeNull();
        typeof(Schema.SchemaBase).ShouldNotBeNull();
        typeof(Provisioning.ProvisioningBase).ShouldNotBeNull();
    }

    [Fact]
    public void RelationTuple_message_carries_object_relation_subject()
    {
        var t = new RelationTuple
        {
            Object = new EntityRef { Type = "category", Id = "drugs" },
            Relation = "dispenser",
            Subject = new SubjectRef { Type = "group", Id = "vets", Relation = "member" },
        };
        t.Relation.ShouldBe("dispenser");
        t.Subject.Relation.ShouldBe("member");
    }
}
```

- [ ] **Step 4: Run to verify**

Run: `dotnet test tests/Custodex.Service.Tests --filter ManagementProtoCompileTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests
git commit -m "feat: add management gRPC contracts and generated stubs"
```

---

### Task 6: Management service implementations + schema JSON mapping

**Files:**
- Create: `src/Custodex.Service/Mapping/SchemaJson.cs`
- Create: `src/Custodex.Service/Services/RelationsGrpcService.cs`
- Create: `src/Custodex.Service/Services/SchemaGrpcService.cs`
- Create: `src/Custodex.Service/Services/ProvisioningGrpcService.cs`
- Modify: `src/Custodex.Service/Program.cs`
- Test: `tests/Custodex.Service.Tests/ManagementServiceTests.cs`

**Interfaces:**
- Produces:
  - `SchemaJson` with `string Serialize(Schema)` and `Schema Deserialize(string)` using `System.Text.Json` with a polymorphic `PermExpr` converter (the AST has an abstract base with sealed subtypes).
  - `RelationsGrpcService : Relations.RelationsBase` over `IRelationManager`; `SchemaGrpcService : Schema.SchemaBase` over `ISchemaManager`; `ProvisioningGrpcService : Provisioning.ProvisioningBase` over `IStoreManager`/`ITenantManager`.
- Consumes: `IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager` (`Custodex.Abstractions`), `ProtoMap` (Task 3), the generated management stubs (Task 5).

> **`PermExpr` polymorphism.** The schema AST's `PermExpr` is an abstract record with sealed subtypes (`RelationRef`/`Union`/`Intersect`/`Exclude`/`Arrow`/`Conditioned`). `System.Text.Json` needs a discriminator to round-trip it. `SchemaJson` registers a `JsonDerivedType` set on `PermExpr` (and on `ConditionExpr` similarly, though M3/01 schemas carry the empty body placeholder from `m0/02`). The validate/set paths exercise `CustodexSchemaManager` (`m1/09`), which throws `SchemaValidationException` on an invalid schema — mapped to a gRPC `InvalidArgument` status.

- [ ] **Step 1: Implement `SchemaJson`**

```csharp
// src/Custodex.Service/Mapping/SchemaJson.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Custodex.Abstractions;

namespace Custodex.Service.Mapping;

/// <summary>
/// Canonical-model JSON serialization for the Schema AST, used by the gRPC/REST management surface
/// until the m3/05 DSL parser lands. Registers the polymorphic discriminators the PermExpr/ConditionExpr
/// hierarchies need so the sealed subtypes round-trip.
/// </summary>
public static class SchemaJson
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    public static string Serialize(Schema schema) => JsonSerializer.Serialize(schema, Options);
    public static Schema Deserialize(string json) =>
        JsonSerializer.Deserialize<Schema>(json, Options)
        ?? throw new JsonException("Schema JSON deserialized to null.");

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { AddPermExprDiscriminators, AddConditionExprDiscriminators },
            },
        };
        return options;
    }

    private static void AddPermExprDiscriminators(JsonTypeInfo info)
    {
        if (info.Type != typeof(PermExpr)) return;
        info.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$kind",
            DerivedTypes =
            {
                new JsonDerivedType(typeof(RelationRef), "relation"),
                new JsonDerivedType(typeof(Union), "union"),
                new JsonDerivedType(typeof(Intersect), "intersect"),
                new JsonDerivedType(typeof(Exclude), "exclude"),
                new JsonDerivedType(typeof(Arrow), "arrow"),
                new JsonDerivedType(typeof(Conditioned), "conditioned"),
            },
        };
    }

    private static void AddConditionExprDiscriminators(JsonTypeInfo info)
    {
        if (info.Type != typeof(ConditionExpr)) return;
        // M3/01 schemas carry the m0/02 EmptyConditionBody placeholder; m0/06 condition bodies and their
        // discriminators are registered here when condition authoring lands over the API.
        info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$kind" };
    }
}
```

> **Condition-body discriminators are a forward seam.** `m0/02`'s `EmptyConditionBody` is the placeholder body; once `m0/06` defines the real `ConditionExpr` nodes and `m3/05` adds the DSL, the concrete `JsonDerivedType` entries are added in `AddConditionExprDiscriminators`. M3/01 schemas validate and round-trip with the placeholder body, which is sufficient for the management surface.

- [ ] **Step 2: Implement the management services**

```csharp
// src/Custodex.Service/Services/RelationsGrpcService.cs
using Grpc.Core;
using Custodex.Abstractions;
using Custodex.Service.Grpc;
using Custodex.Service.Mapping;
using Google.Protobuf.WellKnownTypes;

namespace Custodex.Service.Services;

public sealed class RelationsGrpcService(IRelationManager relations) : Relations.RelationsBase
{
    public override async Task<WriteTuplesResponse> WriteTuples(WriteTuplesRequest request, ServerCallContext context)
    {
        var tuples = request.Tuples.Select(ToTuple).ToList();
        await relations.WriteTuplesAsync(ProtoMap.ToTenantContext(request.Tenant), request.Actor, tuples,
            context.CancellationToken);
        return new WriteTuplesResponse { Count = tuples.Count };
    }

    public override async Task<WriteTuplesResponse> DeleteTuples(WriteTuplesRequest request, ServerCallContext context)
    {
        var tuples = request.Tuples.Select(ToTuple).ToList();
        await relations.DeleteTuplesAsync(ProtoMap.ToTenantContext(request.Tenant), request.Actor, tuples,
            context.CancellationToken);
        return new WriteTuplesResponse { Count = tuples.Count };
    }

    public override async Task<WriteAttributesResponse> WriteAttributes(WriteAttributesRequest request, ServerCallContext context)
    {
        await relations.WriteAttributesAsync(ProtoMap.ToTenantContext(request.Tenant), request.Actor,
            ProtoMap.ToEntityRef(request.Object), ProtoMap.FromStruct(request.Attributes), context.CancellationToken);
        return new WriteAttributesResponse();
    }

    public override async Task<ReadTuplesResponse> ReadTuples(ReadTuplesRequest request, ServerCallContext context)
    {
        var filter = new TupleFilter(
            Empty(request.ObjectType), Empty(request.ObjectId), Empty(request.Relation),
            Empty(request.SubjectType), Empty(request.SubjectId));
        var tuples = await relations.ReadTuplesAsync(ProtoMap.ToTenantContext(request.Tenant), filter,
            context.CancellationToken);
        var response = new ReadTuplesResponse();
        foreach (var t in tuples)
            response.Tuples.Add(FromTuple(t));
        return response;
    }

    public override async Task<ReadChangeLogResponse> ReadChangeLog(ReadChangeLogRequest request, ServerCallContext context)
    {
        var filter = new ChangeLogFilter(
            request.Since?.ToDateTimeOffset(),
            Empty(request.Actor),
            request.Limit <= 0 ? 100 : request.Limit);
        var entries = await relations.ReadChangeLogAsync(ProtoMap.ToTenantContext(request.Tenant), filter,
            context.CancellationToken);
        var response = new ReadChangeLogResponse();
        foreach (var e in entries)
            response.Entries.Add(new ChangeLogEntry
            {
                Id = e.Id, Actor = e.Actor, Operation = e.Operation, Target = e.Target,
                BeforeJson = e.Before?.ToString() ?? "", AfterJson = e.After?.ToString() ?? "",
                OccurredAt = Timestamp.FromDateTimeOffset(e.OccurredAt),
            });
        return response;
    }

    private static RelationTuple ToTuple(Grpc.RelationTuple t) => new(
        ProtoMap.ToEntityRef(t.Object), t.Relation, ProtoMap.ToSubjectRef(t.Subject), ProtoMap.ToConditionRef(t.Condition));

    private static Grpc.RelationTuple FromTuple(RelationTuple t)
    {
        var msg = new Grpc.RelationTuple
        {
            Object = ProtoMap.FromEntityRef(t.Object),
            Relation = t.Relation,
            Subject = ProtoMap.FromSubjectRef(t.Subject),
        };
        if (t.Condition is not null)
            msg.Condition = ProtoMap.FromConditionRef(t.Condition);
        return msg;
    }

    private static string? Empty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
```

```csharp
// src/Custodex.Service/Services/SchemaGrpcService.cs
using Grpc.Core;
using Custodex.Abstractions;
using Custodex.Service.Grpc;
using Custodex.Service.Mapping;

namespace Custodex.Service.Services;

public sealed class SchemaGrpcService(ISchemaManager schemas) : Schema.SchemaBase
{
    public override Task<ValidateSchemaResponse> Validate(ValidateSchemaRequest request, ServerCallContext context)
    {
        var schema = SchemaJson.Deserialize(request.SchemaJson);
        var result = schemas.ValidateSchema(schema);
        var response = new ValidateSchemaResponse { IsValid = result.IsValid };
        response.Errors.AddRange(result.Errors);
        return Task.FromResult(response);
    }

    public override async Task<SetActiveSchemaResponse> SetActive(SetActiveSchemaRequest request, ServerCallContext context)
    {
        var schema = SchemaJson.Deserialize(request.SchemaJson);
        try
        {
            await schemas.SetActiveSchemaAsync(request.Store, schema, context.CancellationToken);
        }
        catch (SchemaValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, string.Join("; ", ex.Errors)));
        }
        return new SetActiveSchemaResponse();
    }

    public override async Task<GetActiveSchemaResponse> GetActive(GetActiveSchemaRequest request, ServerCallContext context)
    {
        var schema = await schemas.GetActiveSchemaAsync(request.Store, context.CancellationToken);
        return schema is null
            ? new GetActiveSchemaResponse { Found = false, SchemaJson = "" }
            : new GetActiveSchemaResponse { Found = true, SchemaJson = SchemaJson.Serialize(schema) };
    }
}
```

```csharp
// src/Custodex.Service/Services/ProvisioningGrpcService.cs
using Grpc.Core;
using Custodex.Abstractions;
using Custodex.Service.Grpc;
using Custodex.Service.Mapping;

namespace Custodex.Service.Services;

public sealed class ProvisioningGrpcService(IStoreManager stores, ITenantManager tenants) : Provisioning.ProvisioningBase
{
    public override async Task<CreateStoreResponse> CreateStore(CreateStoreRequest request, ServerCallContext context)
    {
        await stores.CreateStoreAsync(request.Store, context.CancellationToken);
        return new CreateStoreResponse();
    }

    public override async Task<CreateTenantResponse> CreateTenant(CreateTenantRequest request, ServerCallContext context)
    {
        await tenants.CreateTenantAsync(ProtoMap.ToTenantContext(request.Tenant), context.CancellationToken);
        return new CreateTenantResponse();
    }
}
```

- [ ] **Step 3: Map the management services in `Program.cs`**

Add before `app.Run();`:

```csharp
// src/Custodex.Service/Program.cs — add before app.Run();
app.MapGrpcService<RelationsGrpcService>();
app.MapGrpcService<SchemaGrpcService>();
app.MapGrpcService<ProvisioningGrpcService>();
```

- [ ] **Step 4: Write the failing management end-to-end test**

```csharp
// tests/Custodex.Service.Tests/ManagementServiceTests.cs
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Grpc;
using Custodex.Service.Mapping;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests;

[Collection("service")]
public class ManagementServiceTests(PostgresFixture fx)
{
    private const string Store = "grpc-mgmt";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Custodex:ConnectionString", fx.ConnectionString));

    private static GrpcChannel Channel(WebApplicationFactory<Program> factory) =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        });

    private static string SchemaJsonText() => SchemaJson.Serialize(new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("category", t => t
            .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
            .Permission("record_dispense", p => p.Relation("dispenser")))
        .Build());

    [Fact]
    public async Task Provision_set_schema_write_tuple_then_read_back_round_trips()
    {
        using var factory = CreateFactory();
        var channel = Channel(factory);
        var provisioning = new Provisioning.ProvisioningClient(channel);
        var schemaClient = new Schema.SchemaClient(channel);
        var relations = new Relations.RelationsClient(channel);

        await provisioning.CreateStoreAsync(new CreateStoreRequest { Store = Store });
        await provisioning.CreateTenantAsync(new CreateTenantRequest
        {
            Tenant = new TenantContext { Store = Store, Tenant = "t1" },
        });
        await provisioning.CreateTenantAsync(new CreateTenantRequest
        {
            Tenant = new TenantContext { Store = Store, Tenant = Store }, // schema-audit bookkeeping tenant
        });

        await schemaClient.SetActiveAsync(new SetActiveSchemaRequest { Store = Store, SchemaJson = SchemaJsonText() });
        var fetched = await schemaClient.GetActiveAsync(new GetActiveSchemaRequest { Store = Store });
        fetched.Found.ShouldBeTrue();

        var write = new WriteTuplesRequest { Tenant = new TenantContext { Store = Store, Tenant = "t1" }, Actor = "admin" };
        write.Tuples.Add(new RelationTuple
        {
            Object = new EntityRef { Type = "category", Id = "drugs" },
            Relation = "dispenser",
            Subject = new SubjectRef { Type = "group", Id = "vets", Relation = "member" },
        });
        (await relations.WriteTuplesAsync(write)).Count.ShouldBe(1);

        var read = await relations.ReadTuplesAsync(new ReadTuplesRequest
        {
            Tenant = new TenantContext { Store = Store, Tenant = "t1" }, ObjectType = "category",
        });
        read.Tuples.ShouldContain(t => t.Object.Id == "drugs" && t.Subject.Relation == "member");

        var log = await relations.ReadChangeLogAsync(new ReadChangeLogRequest
        {
            Tenant = new TenantContext { Store = Store, Tenant = "t1" },
        });
        log.Entries.ShouldContain(e => e.Actor == "admin" && e.Operation == "write");
    }

    [Fact]
    public async Task SetActive_with_an_invalid_schema_returns_invalid_argument()
    {
        using var factory = CreateFactory();
        var channel = Channel(factory);
        var provisioning = new Provisioning.ProvisioningClient(channel);
        var schemaClient = new Schema.SchemaClient(channel);

        const string store = "grpc-mgmt-bad";
        await provisioning.CreateStoreAsync(new CreateStoreRequest { Store = store });
        await provisioning.CreateTenantAsync(new CreateTenantRequest
        {
            Tenant = new TenantContext { Store = store, Tenant = store },
        });

        // A permission referencing a relation that does not exist => SchemaValidator fails.
        var bad = SchemaJson.Serialize(new Schema("v1",
            [new EntityTypeDef("doc", [], [new PermissionDef("view", new RelationRef("ghost"))])], []));

        var ex = await Should.ThrowAsync<RpcException>(() =>
            schemaClient.SetActiveAsync(new SetActiveSchemaRequest { Store = store, SchemaJson = bad }).ResponseAsync);
        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }
}
```

- [ ] **Step 5: Run to verify**

Run: `dotnet test tests/Custodex.Service.Tests --filter ManagementServiceTests`
Expected: PASS (2 tests). Provisioning → schema activation → audited tuple write → read-back round-trips over gRPC; an invalid schema surfaces as `InvalidArgument`.

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests
git commit -m "feat: add management gRPC services with schema JSON mapping"
```

---

### Task 7: Startup migrations and final host wiring

**Files:**
- Modify: `src/Custodex.Service/Program.cs`
- Test: `tests/Custodex.Service.Tests/StartupMigrationTests.cs`

**Interfaces:**
- Produces: a host that applies the Postgres migrations at startup (idempotent `MigrationRunner.ApplyAsync` from `m1/01`) so a fresh database is usable on first boot, and maps all four gRPC services.
- Consumes: `MigrationRunner` (`m1/01`).

> **Idempotent and safe to re-run.** `m1/01`'s migrations are idempotent; running them at startup means a freshly provisioned container is ready without a separate migration step. A consumer who manages migrations externally can disable this via configuration (`Custodex:ApplyMigrationsOnStartup`).

- [ ] **Step 1: Add startup migration to `Program.cs`**

Replace the build/run tail of `Program.cs` so migrations run before serving:

```csharp
// src/Custodex.Service/Program.cs — replace from "var app = builder.Build();" downward
var app = builder.Build();

if (builder.Configuration.GetValue("Custodex:ApplyMigrationsOnStartup", true))
{
    await using var conn = new Npgsql.NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await Custodex.Storage.Postgres.MigrationRunner.ApplyAsync(conn);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGrpcService<DecisionGrpcService>();
app.MapGrpcService<RelationsGrpcService>();
app.MapGrpcService<SchemaGrpcService>();
app.MapGrpcService<ProvisioningGrpcService>();

app.Run();

/// <summary>Exposed so WebApplicationFactory&lt;Program&gt; can host the service in tests.</summary>
public partial class Program;
```

- [ ] **Step 2: Write the startup-migration test** (a clean container, no fixture pre-migration)

```csharp
// tests/Custodex.Service.Tests/StartupMigrationTests.cs
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Custodex.Abstractions;
using Testcontainers.PostgreSql;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests;

public class StartupMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Host_applies_migrations_on_startup_and_serves_check()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Custodex:ConnectionString", _container.GetConnectionString()));

        // Building the client forces host startup, which runs migrations on the clean container.
        using var client = factory.CreateClient();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();

        // Provisioning works because the tables now exist.
        using var scope = factory.Services.CreateScope();
        var stores = scope.ServiceProvider.GetRequiredService<IStoreManager>();
        await Should.NotThrowAsync(() => stores.CreateStoreAsync("startup-store"));
    }
}
```

- [ ] **Step 3: Run to verify**

Run: `dotnet test tests/Custodex.Service.Tests --filter StartupMigrationTests`
Expected: PASS. A clean container becomes usable purely from host startup.

- [ ] **Step 4: Run the full service test suite**

Run: `dotnet test tests/Custodex.Service.Tests`
Expected: PASS — host boot, proto compile, ProtoMap, decision, management, and startup migration all green.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests
git commit -m "feat: apply migrations on startup and map all gRPC services"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] `Custodex.Service` references the engine packages and composes it via `AddCustodex().UsePostgres()` exactly as the in-process consumer (`m1/09`); no evaluation/persistence logic lives in the service.
- [ ] The `Custodex.v1` proto declares `Decision`, `Relations`, `Schema`, `Provisioning`, with messages mirroring `EntityRef`/`SubjectRef`/`ConditionRef`/`RequestContext`/`ExplainNode` and `RelationTuple`/filters.
- [ ] `ProtoMap` round-trips every shared type, including the empty-`relation` ⇒ `null` `SubjectRef` rule and the `Struct ⇄ object?` attribute/parameter mapping.
- [ ] `DecisionGrpcService` answers Check/BatchCheck/ListObjects/ListSubjects through `IAuthorizer` over `WebApplicationFactory` + Testcontainers Postgres.
- [ ] The management services provision stores/tenants, validate + activate schema (invalid ⇒ `InvalidArgument`), and write/read audited tuples through the manager interfaces.
- [ ] Migrations apply on startup; the full `Custodex.Service.Tests` suite is green.

## Contract gaps (reported, not changed)

- **No serialized canonical-schema format in the contract.** `ISchemaManager` operates on the `Schema` AST in-process, but the gRPC/REST surface must carry a schema over the wire. This plan introduces `SchemaJson` (a `System.Text.Json` polymorphic serialization of the AST) as the M3 transport until `m3/05`'s DSL parser provides the text form. If the contract owner wants the canonical serialization centralized (so the DSL parser, the service, and any tooling share one (de)serializer), a `SchemaSerializer` in `Custodex.Core` (or `Custodex.Abstractions`) is the clean home — flagged for `m3/05`, which owns the DSL ⇄ `Schema` conversion.
- **`Struct` collapses integer and floating-point attributes to `double`.** `RequestContext.Attributes` and `ConditionRef.Parameters` are `object?`; `google.protobuf.Struct` has a single numeric kind, so integers arrive as `double` after a round-trip. The condition evaluator (`m0/06`) coerces numerics, so checks are unaffected, but a consumer reading attributes back verbatim sees `double`. If exact numeric typing over the wire is required, a richer `CustodexValue` oneof (int64/double/bool/string/null) would replace `Struct` — flagged, not changed, since the engine's behaviour is correct with the coercion.
- **`ChangeLogEntry.Before/After` are `object?` with no defined wire shape.** The contract types them as `object?`; this plan serializes them as JSON strings (`before_json`/`after_json`) for transport. If a structured representation is needed, the contract should define the before/after image type. Flagged, not changed.
