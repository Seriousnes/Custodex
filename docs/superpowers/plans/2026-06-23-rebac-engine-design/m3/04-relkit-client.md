# M3/04 — Relkit gRPC Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `Relkit.Client` package: a gRPC client that implements the canonical `Relkit.Abstractions.IAuthorizer` over the network, so a consumer swaps in-process evaluation for the remote `Relkit.Service` by changing **one** DI registration; plus thin gRPC clients for the management interfaces (`IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager`); and an `AddRelkitClient(address)` DI extension. The client is proven against the real `Relkit.Service` host (via `WebApplicationFactory` + `Grpc.Net.Client`) by asserting it returns the **identical** decisions the in-process `EngineDrivenAuthorizer` returns for the six spec §12 worked examples.

**Architecture:** `Relkit.Client` references `Relkit.Abstractions` (for the `IAuthorizer`/manager interfaces and the canonical request/result records) and the shared proto contract from `m3/01-grpc-contracts.md` (compiled with `Grpc.Tools`). `GrpcAuthorizer : IAuthorizer` holds a generated `Authorizer.AuthorizerClient`, maps the canonical request records to proto messages, calls the service, and maps proto results back to canonical records — so callers see only `Relkit.Abstractions`. A `ProtoMapping` static class owns every record⇄proto conversion in one place (the single source of marshalling truth, shared by the four management clients). `AddRelkitClient(address)` registers the gRPC channel and the four client facades against their `Relkit.Abstractions` interfaces. The DI swap is literal: a consumer who had `AddRelkit().UsePostgres(...)` (in-process, `m1/09`) instead calls `AddRelkitClient("https://...")` and every `IAuthorizer`/manager injection resolves to the remote client with no other code change (spec §4, §10.1).

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly, `Grpc.Net.Client`, `Grpc.Net.ClientFactory`, `Google.Protobuf`, `Grpc.Tools`, `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`).

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 license metadata; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. Depends on `m0/01` (`Relkit.Abstractions` contract), `m3/01` (the proto + `Relkit.Service` host), and reuses `tests/Relkit.Conformance` (`m0/09`) for the six worked examples.

## Shared decisions (locked)

- **The proto is owned by `m3/01`.** `Relkit.Client` and `Relkit.Service` compile the **same** `relkit.proto` (a `<Protobuf>` item pointing at the shared file, `GrpcServices="Client"` here, `GrpcServices="Server"` in the host). This plan reproduces the relevant proto messages verbatim so the mapping is unambiguous; if `m3/01` named a field differently, the proto file is the single source of truth and `ProtoMapping` is adjusted to match (see Contract gaps).
- **`ProtoMapping` is the one marshalling seam.** Every `EntityRef`/`SubjectRef`/`RelationTuple`/`Schema`/condition conversion lives there. The four facades and `GrpcAuthorizer` call it; no conversion logic is duplicated.
- **Deny vs. error preserved over the wire (spec §10.3).** Allow/deny is a normal `CheckResponse.allowed` boolean. The typed exceptions (`UnknownTypeException`, `UnknownRelationException`, `UnknownPermissionException`, `SchemaValidationException`, `EvaluationLimitException`) are carried as gRPC `Status` with a structured detail and **re-thrown** client-side as the same exception type, so a remote caller sees the identical exception a local caller would.
- **`object?` condition params and attribute values** marshal through a `google.protobuf.Value`/`Struct`-shaped JSON payload (the proto from `m3/01` carries them as a `Struct`), so `int`/`long`/`double`/`bool`/`string`/timestamp round-trip. `ProtoMapping` centralizes the boxing rules.

---

### Task 1: Create `Relkit.Client`, reference Abstractions, and compile the shared proto

**Files:**
- Create: `src/Relkit.Client/Relkit.Client.csproj`
- Create: `src/Relkit.Client/Protos/relkit.proto`
- Create: `tests/Relkit.Client.Tests/Relkit.Client.Tests.csproj`
- Test: `tests/Relkit.Client.Tests/ClientWiringTests.cs`

**Interfaces:**
- Produces: the `Relkit.Client` assembly referencing `Relkit.Abstractions` and the generated gRPC client stubs (`Authorizer.AuthorizerClient`, `RelationManager.RelationManagerClient`, `SchemaManager.SchemaManagerClient`, `Provisioning.ProvisioningClient`).
- Consumes: `Relkit.Abstractions`; the proto contract from `m3/01`.

> **Proto source.** The canonical `relkit.proto` is authored in `m3/01`. Copy that file into `src/Relkit.Client/Protos/relkit.proto` (or add it as a linked `<Protobuf Include="..\..\proto\relkit.proto" Link="Protos\relkit.proto" />`). The proto below is the contract this client requires; it MUST match `m3/01` field-for-field. The host and client share it.

- [ ] **Step 1: Create the project and references**

Run:
```bash
dotnet new classlib -n Relkit.Client -o src/Relkit.Client -f net10.0
dotnet new xunit -n Relkit.Client.Tests -o tests/Relkit.Client.Tests -f net10.0
rm src/Relkit.Client/Class1.cs tests/Relkit.Client.Tests/UnitTest1.cs
dotnet sln add src/Relkit.Client tests/Relkit.Client.Tests
dotnet add src/Relkit.Client reference src/Relkit.Abstractions
dotnet add src/Relkit.Client package Grpc.Net.Client
dotnet add src/Relkit.Client package Grpc.Net.ClientFactory
dotnet add src/Relkit.Client package Google.Protobuf
dotnet add src/Relkit.Client package Grpc.Tools
dotnet add src/Relkit.Client package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add tests/Relkit.Client.Tests reference src/Relkit.Client
dotnet add tests/Relkit.Client.Tests reference src/Relkit.Abstractions
dotnet add tests/Relkit.Client.Tests package Shouldly
```

- [ ] **Step 2: Write the shared proto** (the contract `m3/01` owns; reproduced for an unambiguous mapping)

```protobuf
// src/Relkit.Client/Protos/relkit.proto
syntax = "proto3";

option csharp_namespace = "Relkit.Grpc";

package relkit.v1;

import "google/protobuf/struct.proto";
import "google/protobuf/timestamp.proto";

// ---- Shared messages ----

message EntityRef { string type = 1; string id = 2; }
message SubjectRef { string type = 1; string id = 2; string relation = 3; } // relation "" => not a subject-set

message ConditionRef {
  string name = 1;
  google.protobuf.Struct parameters = 2;
}

message RelationTuple {
  EntityRef object = 1;
  string relation = 2;
  SubjectRef subject = 3;
  ConditionRef condition = 4;     // absent => unconditioned
  bool has_condition = 5;
}

message TenantContext { string store = 1; string tenant = 2; }

message RequestContext {
  google.protobuf.Timestamp now = 1;
  SubjectRef subject = 2;
  google.protobuf.Struct attributes = 3;
}

message ExplainNode {
  string description = 1;
  bool allowed = 2;
  repeated ExplainNode children = 3;
}

// ---- Decision API (IAuthorizer) ----

service Authorizer {
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
message CheckResponse { bool allowed = 1; ExplainNode explain = 2; bool has_explain = 3; }

message CheckItem { EntityRef object = 1; string permission = 2; SubjectRef subject = 3; }
message BatchCheckRequest {
  TenantContext tenant = 1;
  repeated CheckItem items = 2;
  RequestContext context = 3;
}
message BatchCheckResponse { repeated CheckResponse results = 1; }

message ListObjectsRequest {
  TenantContext tenant = 1;
  SubjectRef subject = 2;
  string object_type = 3;
  string permission = 4;
  RequestContext context = 5;
  int32 page_size = 6;
  string continuation_token = 7;  // "" => first page
}
message ListObjectsResponse { repeated string object_ids = 1; string continuation_token = 2; }

message ListSubjectsRequest {
  TenantContext tenant = 1;
  EntityRef object = 2;
  string permission = 3;
  RequestContext context = 4;
  int32 page_size = 5;
  string continuation_token = 6;
}
message ListSubjectsResponse { repeated SubjectRef subjects = 1; string continuation_token = 2; }

// ---- Management API ----

service RelationManager {
  rpc WriteTuples (WriteTuplesRequest) returns (WriteTuplesResponse);
  rpc DeleteTuples (WriteTuplesRequest) returns (WriteTuplesResponse);
  rpc WriteAttributes (WriteAttributesRequest) returns (WriteAttributesResponse);
  rpc ReadTuples (ReadTuplesRequest) returns (ReadTuplesResponse);
  rpc ReadChangeLog (ReadChangeLogRequest) returns (ReadChangeLogResponse);
}

message WriteTuplesRequest { TenantContext tenant = 1; string actor = 2; repeated RelationTuple tuples = 3; }
message WriteTuplesResponse {}
message WriteAttributesRequest {
  TenantContext tenant = 1; string actor = 2; EntityRef object = 3; google.protobuf.Struct attributes = 4;
}
message WriteAttributesResponse {}

message TupleFilter {
  string object_type = 1; string object_id = 2; string relation = 3; string subject_type = 4; string subject_id = 5;
}
message ReadTuplesRequest { TenantContext tenant = 1; TupleFilter filter = 2; }
message ReadTuplesResponse { repeated RelationTuple tuples = 1; }

message ChangeLogFilter { google.protobuf.Timestamp since = 1; bool has_since = 2; string actor = 3; int32 limit = 4; }
message ChangeLogEntry {
  int64 id = 1; string actor = 2; string operation = 3; string target = 4;
  google.protobuf.Value before = 5; google.protobuf.Value after = 6; google.protobuf.Timestamp occurred_at = 7;
}
message ReadChangeLogRequest { TenantContext tenant = 1; ChangeLogFilter filter = 2; }
message ReadChangeLogResponse { repeated ChangeLogEntry entries = 1; }

service SchemaManager {
  rpc ValidateSchema (ValidateSchemaRequest) returns (ValidateSchemaResponse);
  rpc SetActiveSchema (SetActiveSchemaRequest) returns (SetActiveSchemaResponse);
  rpc GetActiveSchema (GetActiveSchemaRequest) returns (GetActiveSchemaResponse);
}

// Schema travels as its canonical JSON form (System.Text.Json polymorphic AST, spec §5.5/§6.3 jsonb).
message ValidateSchemaRequest { string schema_json = 1; }
message ValidateSchemaResponse { bool is_valid = 1; repeated string errors = 2; }
message SetActiveSchemaRequest { string store = 1; string schema_json = 2; }
message SetActiveSchemaResponse {}
message GetActiveSchemaRequest { string store = 1; }
message GetActiveSchemaResponse { string schema_json = 1; bool has_schema = 2; }

service Provisioning {
  rpc CreateStore (CreateStoreRequest) returns (CreateStoreResponse);
  rpc CreateTenant (CreateTenantRequest) returns (CreateTenantResponse);
}
message CreateStoreRequest { string store = 1; }
message CreateStoreResponse {}
message CreateTenantRequest { TenantContext tenant = 1; }
message CreateTenantResponse {}
```

- [ ] **Step 3: Register the proto for client codegen** in `Relkit.Client.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Protobuf Include="Protos\relkit.proto" GrpcServices="Client" />
  </ItemGroup>
</Project>
```

> `Directory.Build.props` (from `m0/01`) supplies `Nullable`/`ImplicitUsings`/`TreatWarningsAsErrors`/license. Generated proto code can trip `TreatWarningsAsErrors`; suppress only the generated files by adding `<PropertyGroup><NoWarn>$(NoWarn);CS8981</NoWarn></PropertyGroup>` if the build flags lowercase-type or nullable warnings from `*.g.cs` — keep this scoped and documented.

- [ ] **Step 4: Write the wiring test**

```csharp
// tests/Relkit.Client.Tests/ClientWiringTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Client.Tests;

public class ClientWiringTests
{
    [Fact]
    public void Generated_client_stubs_exist()
    {
        typeof(Relkit.Grpc.Authorizer.AuthorizerClient).ShouldNotBeNull();
        typeof(Relkit.Grpc.RelationManager.RelationManagerClient).ShouldNotBeNull();
        typeof(Relkit.Grpc.SchemaManager.SchemaManagerClient).ShouldNotBeNull();
        typeof(Relkit.Grpc.Provisioning.ProvisioningClient).ShouldNotBeNull();
    }
}
```

- [ ] **Step 5: Run to verify failure**

Run: `dotnet test tests/Relkit.Client.Tests --filter ClientWiringTests`
Expected: FAIL — the proto is not yet compiled / stubs missing until the build runs codegen. (After adding the `<Protobuf>` item the first `dotnet build` generates the stubs; if codegen is misconfigured the test fails to compile.)

- [ ] **Step 6: Build to generate stubs, then run to verify pass**

Run: `dotnet build src/Relkit.Client && dotnet test tests/Relkit.Client.Tests --filter ClientWiringTests`
Expected: PASS (1 test).

- [ ] **Step 7: Commit**

```bash
git add src/Relkit.Client tests/Relkit.Client.Tests
git commit -m "chore: scaffold Relkit.Client with shared proto codegen"
```

---

### Task 2: `ProtoMapping` — record ⇄ proto marshalling

**Files:**
- Create: `src/Relkit.Client/ProtoMapping.cs`
- Test: `tests/Relkit.Client.Tests/ProtoMappingTests.cs`

**Interfaces:**
- Produces: `static class ProtoMapping` with `ToProto`/`ToDomain` overloads for `EntityRef`, `SubjectRef`, `ConditionRef?`, `RelationTuple`, `RequestContext`, `TenantContext`, `ExplainNode?`, `IReadOnlyDictionary<string,object?>` ⇄ `Struct`, and `TupleFilter`/`ChangeLogFilter`/`ChangeLogEntry`.
- Consumes: the canonical records from `Relkit.Abstractions`; the generated `Relkit.Grpc.*` messages; `Google.Protobuf.WellKnownTypes` (`Struct`, `Value`, `Timestamp`).

> **Round-trip is the invariant.** Every `ToDomain(ToProto(x))` must equal `x`. The subject-set marker is `SubjectRef.Relation is null` ⇄ proto `relation == ""`; the wildcard is `Id == "*"` and survives untouched. `ConditionRef` is optional: domain `null` ⇄ proto `has_condition == false`. Attribute/parameter values box as `int`/`long` → `Value` number; `bool` → bool; `string` → string; `double` → number; `DateTimeOffset` → ISO-8601 string (the service decodes the same way). `long`/`int` both map to a JSON number; on decode an integral number returns `long` (the evaluator widens, `m0/06`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Client.Tests/ProtoMappingTests.cs
using Relkit.Abstractions;
using Relkit.Client;
using Shouldly;
using Xunit;

namespace Relkit.Client.Tests;

public class ProtoMappingTests
{
    [Fact]
    public void EntityRef_round_trips_including_wildcard()
    {
        var a = new EntityRef("animal", "EL-001");
        ProtoMapping.ToDomain(ProtoMapping.ToProto(a)).ShouldBe(a);

        var w = new EntityRef("user", "*");
        ProtoMapping.ToDomain(ProtoMapping.ToProto(w)).ShouldBe(w);
    }

    [Fact]
    public void SubjectRef_subject_set_marker_round_trips()
    {
        var set = new SubjectRef("group", "vets", "member");
        var domainSet = ProtoMapping.ToDomain(ProtoMapping.ToProto(set));
        domainSet.ShouldBe(set);
        domainSet.IsSubjectSet.ShouldBeTrue();

        var plain = new SubjectRef("user", "alice");
        var domainPlain = ProtoMapping.ToDomain(ProtoMapping.ToProto(plain));
        domainPlain.ShouldBe(plain);
        domainPlain.IsSubjectSet.ShouldBeFalse();
    }

    [Fact]
    public void Tuple_with_condition_round_trips_with_typed_params()
    {
        var tuple = new RelationTuple(
            new EntityRef("category", "drugs"), "dispenser",
            new SubjectRef("group", "vets", "member"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8L, ["end"] = 18L }));

        var back = ProtoMapping.ToDomain(ProtoMapping.ToProto(tuple));
        back.Object.ShouldBe(tuple.Object);
        back.Relation.ShouldBe("dispenser");
        back.Subject.ShouldBe(tuple.Subject);
        back.Condition!.Name.ShouldBe("within_hours");
        back.Condition.Parameters["start"].ShouldBe(8L);
        back.Condition.Parameters["end"].ShouldBe(18L);
    }

    [Fact]
    public void Unconditioned_tuple_has_no_condition()
    {
        var tuple = new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice"));
        ProtoMapping.ToDomain(ProtoMapping.ToProto(tuple)).Condition.ShouldBeNull();
    }

    [Fact]
    public void Attributes_struct_round_trips_typed_values()
    {
        var attrs = new Dictionary<string, object?>
        {
            ["weight"] = 50L, ["name"] = "Ellie", ["active"] = true, ["ratio"] = 1.5,
        };
        var back = ProtoMapping.AttributesToDomain(ProtoMapping.AttributesToProto(attrs));
        back["weight"].ShouldBe(50L);
        back["name"].ShouldBe("Ellie");
        back["active"].ShouldBe(true);
        back["ratio"].ShouldBe(1.5);
    }

    [Fact]
    public void RequestContext_round_trips_now_and_subject()
    {
        var ctx = new RequestContext(
            new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero),
            new SubjectRef("user", "dr-smith"), new Dictionary<string, object?>());
        var back = ProtoMapping.ToDomain(ProtoMapping.ToProto(ctx));
        back.Now.ShouldBe(ctx.Now);
        back.Subject.ShouldBe(ctx.Subject);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Client.Tests --filter ProtoMappingTests`
Expected: FAIL — `ProtoMapping` does not exist.

- [ ] **Step 3: Implement `ProtoMapping`**

```csharp
// src/Relkit.Client/ProtoMapping.cs
using Google.Protobuf.WellKnownTypes;
using Relkit.Abstractions;
using G = Relkit.Grpc;

namespace Relkit.Client;

/// <summary>The single marshalling seam between the canonical Relkit.Abstractions records and the gRPC wire messages.</summary>
public static class ProtoMapping
{
    // ---- EntityRef ----
    public static G.EntityRef ToProto(EntityRef e) => new() { Type = e.Type, Id = e.Id };
    public static EntityRef ToDomain(G.EntityRef e) => new(e.Type, e.Id);

    // ---- SubjectRef ----  (Relation "" on the wire => null in the domain => not a subject-set)
    public static G.SubjectRef ToProto(SubjectRef s) =>
        new() { Type = s.Type, Id = s.Id, Relation = s.Relation ?? "" };
    public static SubjectRef ToDomain(G.SubjectRef s) =>
        new(s.Type, s.Id, string.IsNullOrEmpty(s.Relation) ? null : s.Relation);

    // ---- ConditionRef (optional) ----
    public static G.RelationTuple ToProto(RelationTuple t)
    {
        var msg = new G.RelationTuple
        {
            Object = ToProto(t.Object), Relation = t.Relation, Subject = ToProto(t.Subject),
            HasCondition = t.Condition is not null,
        };
        if (t.Condition is { } c)
            msg.Condition = new G.ConditionRef { Name = c.Name, Parameters = AttributesToProto(c.Parameters) };
        return msg;
    }

    public static RelationTuple ToDomain(G.RelationTuple t)
    {
        ConditionRef? condition = t.HasCondition && t.Condition is not null
            ? new ConditionRef(t.Condition.Name, AttributesToDomain(t.Condition.Parameters))
            : null;
        return new RelationTuple(ToDomain(t.Object), t.Relation, ToDomain(t.Subject), condition);
    }

    // ---- TenantContext ----
    public static G.TenantContext ToProto(TenantContext t) => new() { Store = t.Store, Tenant = t.Tenant };
    public static TenantContext ToDomain(G.TenantContext t) => new(t.Store, t.Tenant);

    // ---- RequestContext ----
    public static G.RequestContext ToProto(RequestContext c) => new()
    {
        Now = Timestamp.FromDateTimeOffset(c.Now),
        Subject = ToProto(c.Subject),
        Attributes = AttributesToProto(c.Attributes),
    };
    public static RequestContext ToDomain(G.RequestContext c) => new(
        c.Now.ToDateTimeOffset(), ToDomain(c.Subject), AttributesToDomain(c.Attributes));

    // ---- ExplainNode (optional) ----
    public static G.ExplainNode? ToProto(ExplainNode? n)
    {
        if (n is null) return null;
        var msg = new G.ExplainNode { Description = n.Description, Allowed = n.Allowed };
        foreach (var child in n.Children)
            msg.Children.Add(ToProto(child));
        return msg;
    }
    public static ExplainNode ToDomain(G.ExplainNode n) =>
        new(n.Description, n.Allowed, n.Children.Select(ToDomain).ToList());

    // ---- Attributes / params  (Struct <-> IReadOnlyDictionary<string, object?>) ----
    public static Struct AttributesToProto(IReadOnlyDictionary<string, object?> attrs)
    {
        var s = new Struct();
        foreach (var (k, v) in attrs)
            s.Fields[k] = ToValue(v);
        return s;
    }

    public static IReadOnlyDictionary<string, object?> AttributesToDomain(Struct? s)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (s is null) return d;
        foreach (var (k, v) in s.Fields)
            d[k] = FromValue(v);
        return d;
    }

    private static Value ToValue(object? v) => v switch
    {
        null => Value.ForNull(),
        bool b => Value.ForBool(b),
        int i => Value.ForNumber(i),
        long l => Value.ForNumber(l),
        double d => Value.ForNumber(d),
        float f => Value.ForNumber(f),
        string str => Value.ForString(str),
        DateTimeOffset dto => Value.ForString(dto.ToString("o")),
        DateTime dt => Value.ForString(new DateTimeOffset(dt).ToString("o")),
        _ => Value.ForString(v.ToString() ?? ""),
    };

    private static object? FromValue(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.NullValue => null,
        Value.KindOneofCase.BoolValue => v.BoolValue,
        // Integral numbers come back as long (the condition evaluator widens to double when needed, m0/06).
        Value.KindOneofCase.NumberValue => v.NumberValue % 1 == 0
            ? (object)(long)v.NumberValue : v.NumberValue,
        Value.KindOneofCase.StringValue => v.StringValue,
        _ => null,
    };

    // ---- TupleFilter ----
    public static G.TupleFilter ToProto(TupleFilter f) => new()
    {
        ObjectType = f.ObjectType ?? "", ObjectId = f.ObjectId ?? "", Relation = f.Relation ?? "",
        SubjectType = f.SubjectType ?? "", SubjectId = f.SubjectId ?? "",
    };

    // ---- ChangeLogFilter / ChangeLogEntry ----
    public static G.ChangeLogFilter ToProto(ChangeLogFilter f)
    {
        var msg = new G.ChangeLogFilter { Actor = f.Actor ?? "", Limit = f.Limit, HasSince = f.Since is not null };
        if (f.Since is { } since) msg.Since = Timestamp.FromDateTimeOffset(since);
        return msg;
    }

    public static ChangeLogEntry ToDomain(G.ChangeLogEntry e) => new(
        e.Id, e.Actor, e.Operation, e.Target,
        e.Before is null || e.Before.KindCase == Value.KindOneofCase.NullValue ? null : FromValue(e.Before),
        e.After is null || e.After.KindCase == Value.KindOneofCase.NullValue ? null : FromValue(e.After),
        e.OccurredAt.ToDateTimeOffset());
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Client.Tests --filter ProtoMappingTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Client tests/Relkit.Client.Tests
git commit -m "feat: add ProtoMapping record-proto marshalling seam"
```

---

### Task 3: `RemoteStatus` — typed-exception preservation over the wire

**Files:**
- Create: `src/Relkit.Client/RemoteStatus.cs`
- Test: `tests/Relkit.Client.Tests/RemoteStatusTests.cs`

**Interfaces:**
- Produces: `static class RemoteStatus` with `T Unwrap<T>(Func<T> grpcCall)` and `Task<T> UnwrapAsync<T>(Func<Task<T>> grpcCall)` that catch `RpcException`, read the `relkit-error` trailer/metadata key, and re-throw the matching `Relkit.Abstractions` exception (`UnknownTypeException`, `UnknownRelationException`, `UnknownPermissionException`, `SchemaValidationException`, `EvaluationLimitException`), otherwise rethrow the `RpcException`.
- Consumes: `Grpc.Core.RpcException`/`StatusCode`/`Metadata`; the exception hierarchy from `m0/01`.

> **Error model (spec §10.3).** The service maps each typed exception to `StatusCode.InvalidArgument` (caller bug) or `FailedPrecondition` (schema) and stamps a trailer `relkit-error-kind` (e.g. `"unknown_type"`) plus detail trailers (`relkit-error-type`, `relkit-error-relation`, `relkit-error-permission`, `relkit-error-detail`, repeated `relkit-error-message` for validation errors). The client reconstructs the exact exception so a remote caller sees what a local caller sees. The exact trailer keys are owned by `m3/01`'s host; this plan reproduces them and adjusts if `m3/01` differs (Contract gaps).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Client.Tests/RemoteStatusTests.cs
using Grpc.Core;
using Relkit.Abstractions;
using Relkit.Client;
using Shouldly;
using Xunit;

namespace Relkit.Client.Tests;

public class RemoteStatusTests
{
    private static RpcException Rpc(StatusCode code, params (string Key, string Value)[] trailers)
    {
        var meta = new Metadata();
        foreach (var (k, v) in trailers) meta.Add(k, v);
        return new RpcException(new Status(code, "remote"), meta);
    }

    [Fact]
    public void Unknown_type_trailer_rethrows_UnknownTypeException()
    {
        var ex = Should.Throw<UnknownTypeException>(() => RemoteStatus.Unwrap<int>(() =>
            throw Rpc(StatusCode.InvalidArgument,
                ("relkit-error-kind", "unknown_type"), ("relkit-error-type", "dragon"))));
        ex.Type.ShouldBe("dragon");
    }

    [Fact]
    public void Unknown_permission_trailer_rethrows_UnknownPermissionException()
    {
        var ex = Should.Throw<UnknownPermissionException>(() => RemoteStatus.Unwrap<int>(() =>
            throw Rpc(StatusCode.InvalidArgument,
                ("relkit-error-kind", "unknown_permission"),
                ("relkit-error-type", "animal"), ("relkit-error-permission", "fly"))));
        ex.Type.ShouldBe("animal");
        ex.Permission.ShouldBe("fly");
    }

    [Fact]
    public void Schema_validation_trailers_rethrow_SchemaValidationException_with_errors()
    {
        var ex = Should.Throw<SchemaValidationException>(() => RemoteStatus.Unwrap<int>(() =>
            throw Rpc(StatusCode.FailedPrecondition,
                ("relkit-error-kind", "schema_invalid"),
                ("relkit-error-message", "dangling relation 'x'"),
                ("relkit-error-message", "non-terminating recursion"))));
        ex.Errors.Count.ShouldBe(2);
        ex.Errors.ShouldContain("dangling relation 'x'");
    }

    [Fact]
    public void Unrecognized_rpc_exception_is_rethrown_as_is()
    {
        Should.Throw<RpcException>(() => RemoteStatus.Unwrap<int>(() =>
            throw Rpc(StatusCode.Unavailable)));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Client.Tests --filter RemoteStatusTests`
Expected: FAIL — `RemoteStatus` does not exist.

- [ ] **Step 3: Implement `RemoteStatus`**

```csharp
// src/Relkit.Client/RemoteStatus.cs
using Grpc.Core;
using Relkit.Abstractions;

namespace Relkit.Client;

/// <summary>
/// Reconstructs Relkit.Abstractions typed exceptions from the structured gRPC trailers the service
/// stamps (spec §10.3), so a remote caller sees the same exception a local caller would. Unrecognized
/// RpcExceptions are rethrown untouched.
/// </summary>
public static class RemoteStatus
{
    public static T Unwrap<T>(Func<T> grpcCall)
    {
        try { return grpcCall(); }
        catch (RpcException ex) { throw Translate(ex); }
    }

    public static async Task<T> UnwrapAsync<T>(Func<Task<T>> grpcCall)
    {
        try { return await grpcCall().ConfigureAwait(false); }
        catch (RpcException ex) { throw Translate(ex); }
    }

    private static Exception Translate(RpcException ex)
    {
        var kind = Get(ex, "relkit-error-kind");
        return kind switch
        {
            "unknown_type" => new UnknownTypeException(Get(ex, "relkit-error-type") ?? ""),
            "unknown_relation" => new UnknownRelationException(
                Get(ex, "relkit-error-type") ?? "", Get(ex, "relkit-error-relation") ?? ""),
            "unknown_permission" => new UnknownPermissionException(
                Get(ex, "relkit-error-type") ?? "", Get(ex, "relkit-error-permission") ?? ""),
            "schema_invalid" => new SchemaValidationException(GetAll(ex, "relkit-error-message")),
            "evaluation_limit" => new EvaluationLimitException(Get(ex, "relkit-error-detail") ?? "limit tripped"),
            _ => ex,   // not a recognized Relkit error: surface the transport failure as-is
        };
    }

    private static string? Get(RpcException ex, string key) =>
        ex.Trailers.GetValue(key);

    private static IReadOnlyList<string> GetAll(RpcException ex, string key) =>
        ex.Trailers.Where(e => e.Key == key).Select(e => e.Value).ToList();
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Client.Tests --filter RemoteStatusTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Client tests/Relkit.Client.Tests
git commit -m "feat: add RemoteStatus typed-exception translation from gRPC trailers"
```

---

### Task 4: `GrpcAuthorizer : IAuthorizer`

**Files:**
- Create: `src/Relkit.Client/GrpcAuthorizer.cs`
- Test: `tests/Relkit.Client.Tests/GrpcAuthorizerMappingTests.cs`

**Interfaces:**
- Produces: `GrpcAuthorizer(Relkit.Grpc.Authorizer.AuthorizerClient client) : IAuthorizer` — `CheckAsync`/`BatchCheckAsync`/`ListObjectsAsync`/`ListSubjectsAsync`, each mapping the canonical request to proto, calling the stub, unwrapping typed errors via `RemoteStatus`, and mapping the response back.
- Consumes: the generated `AuthorizerClient`; `ProtoMapping`; `RemoteStatus`; the `IAuthorizer` contract + request/result records (`m0/01`).

> The constructor takes the generated client (not a channel) so a unit test can pass a stub built over an in-memory channel; the DI extension (Task 7) constructs the client from a channel. `ListObjectsRequest.ContinuationToken == null` maps to proto `""`; a proto `""` response token maps back to `null` (no more pages).

- [ ] **Step 1: Write the failing tests** (mapping is asserted via an in-process server stub in Task 6; here we assert request shaping with a fake client)

```csharp
// tests/Relkit.Client.Tests/GrpcAuthorizerMappingTests.cs
using Grpc.Core;
using Relkit.Abstractions;
using Relkit.Client;
using Relkit.Grpc;
using Shouldly;
using Xunit;

namespace Relkit.Client.Tests;

public class GrpcAuthorizerMappingTests
{
    // A fake client capturing the outgoing request and returning a canned response.
    private sealed class FakeAuthorizerClient : Authorizer.AuthorizerClient
    {
        public CheckRequest? LastCheck;
        public ListObjectsRequest? LastList;

        public override AsyncUnaryCall<CheckResponse> CheckAsync(
            CheckRequest request, CallOptions options)
        {
            LastCheck = request;
            return Fake(new CheckResponse { Allowed = true });
        }

        public override AsyncUnaryCall<ListObjectsResponse> ListObjectsAsync(
            ListObjectsRequest request, CallOptions options)
        {
            LastList = request;
            var resp = new ListObjectsResponse { ContinuationToken = "" };
            resp.ObjectIds.Add("kangaroo");
            resp.ObjectIds.Add("wallaby");
            return Fake(resp);
        }

        private static AsyncUnaryCall<T> Fake<T>(T value) => new(
            Task.FromResult(value), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    private static RequestContext Ctx() =>
        new(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>());

    [Fact]
    public async Task CheckAsync_maps_request_and_returns_allowed()
    {
        var fake = new FakeAuthorizerClient();
        var auth = new GrpcAuthorizer(fake);

        var result = await auth.CheckAsync(new CheckRequest(
            new TenantContext("zoo", "t1"), new EntityRef("species", "kangaroo"), "edit",
            new SubjectRef("user", "alice"), Ctx()));

        result.Allowed.ShouldBeTrue();
        fake.LastCheck!.Object.Type.ShouldBe("species");
        fake.LastCheck.Object.Id.ShouldBe("kangaroo");
        fake.LastCheck.Permission.ShouldBe("edit");
        fake.LastCheck.Tenant.Store.ShouldBe("zoo");
    }

    [Fact]
    public async Task ListObjectsAsync_maps_null_token_to_empty_and_empty_back_to_null()
    {
        var fake = new FakeAuthorizerClient();
        var auth = new GrpcAuthorizer(fake);

        var result = await auth.ListObjectsAsync(new ListObjectsRequest(
            new TenantContext("zoo", "t1"), new SubjectRef("user", "alice"), "species", "edit", Ctx()));

        fake.LastList!.ContinuationToken.ShouldBe("");            // null -> ""
        result.ObjectIds.ShouldBe(new[] { "kangaroo", "wallaby" });
        result.ContinuationToken.ShouldBeNull();                 // "" -> null
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Client.Tests --filter GrpcAuthorizerMappingTests`
Expected: FAIL — `GrpcAuthorizer` does not exist.

- [ ] **Step 3: Implement `GrpcAuthorizer`**

```csharp
// src/Relkit.Client/GrpcAuthorizer.cs
using Relkit.Abstractions;
using G = Relkit.Grpc;

namespace Relkit.Client;

/// <summary>
/// gRPC implementation of <see cref="IAuthorizer"/>. A consumer swaps in-process evaluation for the
/// remote Relkit.Service by registering this type for <see cref="IAuthorizer"/> (spec §4, §10.1) — no
/// caller code changes. Maps canonical records to proto, calls the service, restores typed exceptions.
/// </summary>
public sealed class GrpcAuthorizer(G.Authorizer.AuthorizerClient client) : IAuthorizer
{
    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.CheckRequest
            {
                Tenant = ProtoMapping.ToProto(request.Tenant),
                Object = ProtoMapping.ToProto(request.Object),
                Permission = request.Permission,
                Subject = ProtoMapping.ToProto(request.Subject),
                Context = ProtoMapping.ToProto(request.Context),
                Explain = request.Explain,
            };
            var resp = await client.CheckAsync(req, cancellationToken: ct);
            var explain = resp.HasExplain && resp.Explain is not null
                ? ProtoMapping.ToDomain(resp.Explain) : null;
            return new CheckResult(resp.Allowed, explain);
        });

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.BatchCheckRequest
            {
                Tenant = ProtoMapping.ToProto(request.Tenant),
                Context = ProtoMapping.ToProto(request.Context),
            };
            foreach (var item in request.Items)
                req.Items.Add(new G.CheckItem
                {
                    Object = ProtoMapping.ToProto(item.Object),
                    Permission = item.Permission,
                    Subject = ProtoMapping.ToProto(item.Subject),
                });
            var resp = await client.BatchCheckAsync(req, cancellationToken: ct);
            IReadOnlyList<CheckResult> results = resp.Results
                .Select(r => new CheckResult(r.Allowed,
                    r.HasExplain && r.Explain is not null ? ProtoMapping.ToDomain(r.Explain) : null))
                .ToList();
            return results;
        });

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.ListObjectsRequest
            {
                Tenant = ProtoMapping.ToProto(request.Tenant),
                Subject = ProtoMapping.ToProto(request.Subject),
                ObjectType = request.ObjectType,
                Permission = request.Permission,
                Context = ProtoMapping.ToProto(request.Context),
                PageSize = request.PageSize,
                ContinuationToken = request.ContinuationToken ?? "",
            };
            var resp = await client.ListObjectsAsync(req, cancellationToken: ct);
            return new ListObjectsResult(
                resp.ObjectIds.ToList(),
                string.IsNullOrEmpty(resp.ContinuationToken) ? null : resp.ContinuationToken);
        });

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.ListSubjectsRequest
            {
                Tenant = ProtoMapping.ToProto(request.Tenant),
                Object = ProtoMapping.ToProto(request.Object),
                Permission = request.Permission,
                Context = ProtoMapping.ToProto(request.Context),
                PageSize = request.PageSize,
                ContinuationToken = request.ContinuationToken ?? "",
            };
            var resp = await client.ListSubjectsAsync(req, cancellationToken: ct);
            IReadOnlyList<SubjectRef> subjects = resp.Subjects.Select(ProtoMapping.ToDomain).ToList();
            return new ListSubjectsResult(subjects,
                string.IsNullOrEmpty(resp.ContinuationToken) ? null : resp.ContinuationToken);
        });
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Client.Tests --filter GrpcAuthorizerMappingTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Client tests/Relkit.Client.Tests
git commit -m "feat: add GrpcAuthorizer implementing IAuthorizer over gRPC"
```

---

### Task 5: Management client facades + `SchemaJson` serialization

**Files:**
- Create: `src/Relkit.Client/SchemaJson.cs`
- Create: `src/Relkit.Client/GrpcRelationManager.cs`
- Create: `src/Relkit.Client/GrpcSchemaManager.cs`
- Create: `src/Relkit.Client/GrpcProvisioning.cs`
- Test: `tests/Relkit.Client.Tests/SchemaJsonTests.cs`

**Interfaces:**
- Produces:
  - `static class SchemaJson` with `string Serialize(Schema)` and `Schema Deserialize(string)` using `System.Text.Json` with the polymorphic `PermExpr`/`ConditionExpr` configuration (spec §5.5 / §6.3 jsonb). Round-trips the canonical AST.
  - `GrpcRelationManager(Relkit.Grpc.RelationManager.RelationManagerClient) : IRelationManager`.
  - `GrpcSchemaManager(Relkit.Grpc.SchemaManager.SchemaManagerClient) : ISchemaManager`.
  - `GrpcStoreManager` / `GrpcTenantManager` over `Relkit.Grpc.Provisioning.ProvisioningClient`, implementing `IStoreManager` / `ITenantManager`.
- Consumes: `ProtoMapping`, `RemoteStatus`, the generated stubs, and the manager contracts from `m0/01`.

> **`SchemaJson` mirrors the service's serializer.** The `Schema` AST carries `System.Text.Json` polymorphism for jsonb (per the contract and `m1/01`). The host serializes the same way, so a schema sent as JSON deserializes identically server-side. The exact `JsonSerializerOptions` / `[JsonPolymorphic]` discriminators are owned by the schema-serialization plan; this client uses the same options class. If the engine exposes a canonical `SchemaSerializer` in `Relkit.Core`, `SchemaJson` forwards to it instead of duplicating the configuration (Contract gaps).
> **`ISchemaManager.ValidateSchema` is synchronous** in the contract; the gRPC call is blocking-wrapped (`.GetAwaiter().GetResult()` on the unary call) to honour the interface. `SetActiveSchemaAsync`/`GetActiveSchemaAsync` are async.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Client.Tests/SchemaJsonTests.cs
using Relkit.Abstractions;
using Relkit.Client;
using Relkit.Core;
using Shouldly;
using Xunit;

namespace Relkit.Client.Tests;

public class SchemaJsonTests
{
    [Fact]
    public void Schema_round_trips_through_json_preserving_perm_algebra()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("animal", t => t
                .Relation("medicator", s => s.User().SubjectSet("group", "member"))
                .Relation("enclosure", s => s.Type("enclosure"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p
                    .Relation("medicator").Arrow("enclosure", "edit").Exclude(x => x.Relation("blocked"))))
            .Build();

        var back = SchemaJson.Deserialize(SchemaJson.Serialize(schema));
        back.ShouldBe(schema);   // records => structural equality across the whole AST
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Client.Tests --filter SchemaJsonTests`
Expected: FAIL — `SchemaJson` does not exist.

- [ ] **Step 3: Implement `SchemaJson`** (reference `Relkit.Core` for the AST + builder used in tests)

Run:
```bash
dotnet add src/Relkit.Client reference src/Relkit.Core
dotnet add tests/Relkit.Client.Tests reference src/Relkit.Core
```

```csharp
// src/Relkit.Client/SchemaJson.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using Relkit.Abstractions;
using Relkit.Core.Conditions;

namespace Relkit.Client;

/// <summary>
/// Serializes the canonical <see cref="Schema"/> AST to/from JSON for the gRPC schema messages, using
/// the same System.Text.Json polymorphism the service uses for jsonb (spec §5.5 / §6.3). PermExpr and
/// ConditionExpr are abstract; each concrete node carries a "$kind" discriminator.
/// </summary>
public static class SchemaJson
{
    public static readonly JsonSerializerOptions Options = Build();

    public static string Serialize(Schema schema) => JsonSerializer.Serialize(schema, Options);
    public static Schema Deserialize(string json) =>
        JsonSerializer.Deserialize<Schema>(json, Options)
        ?? throw new JsonException("Schema JSON deserialized to null.");

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            .WithPolymorphism<PermExpr>(
                (typeof(RelationRef), "relation"),
                (typeof(Union), "union"),
                (typeof(Intersect), "intersect"),
                (typeof(Exclude), "exclude"),
                (typeof(Arrow), "arrow"),
                (typeof(Conditioned), "conditioned"))
            .WithPolymorphism<ConditionExpr>(
                (typeof(LiteralBool), "litBool"),
                (typeof(LiteralInt), "litInt"),
                (typeof(LiteralDouble), "litDouble"),
                (typeof(LiteralString), "litString"),
                (typeof(ParamRef), "param"),
                (typeof(AttributeRef), "attribute"),
                (typeof(ContextNow), "now"),
                (typeof(ContextSubject), "subject"),
                (typeof(Compare), "compare"),
                (typeof(BoolOp), "boolOp"),
                (typeof(Not), "not"),
                (typeof(Arithmetic), "arithmetic"),
                (typeof(InList), "inList"),
                (typeof(HourOf), "hourOf"),
                (typeof(EmptyConditionBody), "empty"));
        return options;
    }
}

file static class PolymorphismExtensions
{
    public static System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver WithPolymorphism<TBase>(
        this DefaultJsonTypeInfoResolver resolver, params (Type Type, string Discriminator)[] subtypes)
    {
        var baseType = typeof(TBase);
        var original = (System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)resolver;
        return new DelegatingResolver(original, (type, opts) =>
        {
            var info = original.GetTypeInfo(type, opts);
            if (info is not null && type == baseType)
            {
                info.PolymorphismOptions = new System.Text.Json.Serialization.Metadata.JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "$kind",
                    IgnoreUnrecognizedTypeDiscriminators = false,
                    UnknownDerivedTypeHandling =
                        System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FailSerialization,
                };
                foreach (var (subtype, discriminator) in subtypes)
                    info.PolymorphismOptions.DerivedTypes.Add(
                        new System.Text.Json.Serialization.Metadata.JsonDerivedType(subtype, discriminator));
            }
            return info;
        });
    }
}

file sealed class DelegatingResolver(
    System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver inner,
    Func<Type, JsonSerializerOptions, System.Text.Json.Serialization.Metadata.JsonTypeInfo?> decorate)
    : System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver
{
    public System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(
        Type type, JsonSerializerOptions options) => decorate(type, options) ?? inner.GetTypeInfo(type, options);
}
```

> **Simpler alternative if the engine ships a serializer.** If `Relkit.Core` (or `m1/01`) exposes `SchemaSerializer.Serialize/Deserialize` with the polymorphic config, delete the resolver plumbing above and forward `SchemaJson.Serialize/Deserialize` to it. Prefer that — one serializer, one set of discriminators, guaranteed to match the host. The fallback resolver here exists so the client compiles and round-trips even before that shared serializer lands; the discriminator strings must then match the host's.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Client.Tests --filter SchemaJsonTests`
Expected: PASS (1 test). Records give structural equality, so the whole AST (including the `Exclude(Union(...))` tree) must survive the round-trip.

- [ ] **Step 5: Implement the management facades**

```csharp
// src/Relkit.Client/GrpcRelationManager.cs
using Relkit.Abstractions;
using G = Relkit.Grpc;

namespace Relkit.Client;

public sealed class GrpcRelationManager(G.RelationManager.RelationManagerClient client) : IRelationManager
{
    public Task WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.WriteTuplesRequest { Tenant = ProtoMapping.ToProto(tenant), Actor = actor };
            foreach (var t in tuples) req.Tuples.Add(ProtoMapping.ToProto(t));
            await client.WriteTuplesAsync(req, cancellationToken: ct);
            return 0;
        });

    public Task DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.WriteTuplesRequest { Tenant = ProtoMapping.ToProto(tenant), Actor = actor };
            foreach (var t in tuples) req.Tuples.Add(ProtoMapping.ToProto(t));
            await client.DeleteTuplesAsync(req, cancellationToken: ct);
            return 0;
        });

    public Task WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.WriteAttributesRequest
            {
                Tenant = ProtoMapping.ToProto(tenant), Actor = actor,
                Object = ProtoMapping.ToProto(obj), Attributes = ProtoMapping.AttributesToProto(attributes),
            };
            await client.WriteAttributesAsync(req, cancellationToken: ct);
            return 0;
        });

    public Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(TenantContext tenant, TupleFilter filter, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.ReadTuplesRequest { Tenant = ProtoMapping.ToProto(tenant), Filter = ProtoMapping.ToProto(filter) };
            var resp = await client.ReadTuplesAsync(req, cancellationToken: ct);
            IReadOnlyList<RelationTuple> tuples = resp.Tuples.Select(ProtoMapping.ToDomain).ToList();
            return tuples;
        });

    public Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var req = new G.ReadChangeLogRequest { Tenant = ProtoMapping.ToProto(tenant), Filter = ProtoMapping.ToProto(filter) };
            var resp = await client.ReadChangeLogAsync(req, cancellationToken: ct);
            IReadOnlyList<ChangeLogEntry> entries = resp.Entries.Select(ProtoMapping.ToDomain).ToList();
            return entries;
        });
}
```

```csharp
// src/Relkit.Client/GrpcSchemaManager.cs
using Relkit.Abstractions;
using G = Relkit.Grpc;

namespace Relkit.Client;

public sealed class GrpcSchemaManager(G.SchemaManager.SchemaManagerClient client) : ISchemaManager
{
    public SchemaValidationResult ValidateSchema(Schema schema)
        => RemoteStatus.Unwrap(() =>
        {
            var resp = client.ValidateSchema(new G.ValidateSchemaRequest { SchemaJson = SchemaJson.Serialize(schema) });
            return new SchemaValidationResult(resp.IsValid, resp.Errors.ToList());
        });

    public Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            await client.SetActiveSchemaAsync(
                new G.SetActiveSchemaRequest { Store = store, SchemaJson = SchemaJson.Serialize(schema) },
                cancellationToken: ct);
            return 0;
        });

    public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            var resp = await client.GetActiveSchemaAsync(new G.GetActiveSchemaRequest { Store = store }, cancellationToken: ct);
            return resp.HasSchema ? SchemaJson.Deserialize(resp.SchemaJson) : (Schema?)null;
        });
}
```

```csharp
// src/Relkit.Client/GrpcProvisioning.cs
using Relkit.Abstractions;
using G = Relkit.Grpc;

namespace Relkit.Client;

public sealed class GrpcStoreManager(G.Provisioning.ProvisioningClient client) : IStoreManager
{
    public Task CreateStoreAsync(string store, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            await client.CreateStoreAsync(new G.CreateStoreRequest { Store = store }, cancellationToken: ct);
            return 0;
        });
}

public sealed class GrpcTenantManager(G.Provisioning.ProvisioningClient client) : ITenantManager
{
    public Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default)
        => RemoteStatus.UnwrapAsync(async () =>
        {
            await client.CreateTenantAsync(
                new G.CreateTenantRequest { Tenant = ProtoMapping.ToProto(tenant) }, cancellationToken: ct);
            return 0;
        });
}
```

- [ ] **Step 6: Build to verify the facades compile**

Run: `dotnet build src/Relkit.Client`
Expected: clean build (the facades are exercised end-to-end in Task 6).

- [ ] **Step 7: Commit**

```bash
git add src/Relkit.Client tests/Relkit.Client.Tests
git commit -m "feat: add SchemaJson and gRPC management client facades"
```

---

### Task 6: `AddRelkitClient(address)` DI extension

**Files:**
- Create: `src/Relkit.Client/RelkitClientServiceCollectionExtensions.cs`
- Test: `tests/Relkit.Client.Tests/AddRelkitClientTests.cs`

**Interfaces:**
- Produces: `IServiceCollection AddRelkitClient(this IServiceCollection services, string address)` and an overload `AddRelkitClient(this IServiceCollection services, Uri address)` registering a single `GrpcChannel`, the four generated clients, and `GrpcAuthorizer`/`GrpcRelationManager`/`GrpcSchemaManager`/`GrpcStoreManager`/`GrpcTenantManager` against `IAuthorizer`/`IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager`.
- Consumes: `Grpc.Net.Client.GrpcChannel`; `Microsoft.Extensions.DependencyInjection`.

> **The one-line swap (spec §10.1).** A consumer who used the in-process engine (`AddRelkit().UsePostgres(...)`, `m1/09`) replaces that with `services.AddRelkitClient("https://relkit.internal:443")`. Every `IAuthorizer`/manager injection now resolves to the remote client. Callers are unchanged because both register the same `Relkit.Abstractions` interfaces.

- [ ] **Step 1: Add the DI package and write the failing test**

Run:
```bash
dotnet add src/Relkit.Client package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add tests/Relkit.Client.Tests package Microsoft.Extensions.DependencyInjection
```

```csharp
// tests/Relkit.Client.Tests/AddRelkitClientTests.cs
using Microsoft.Extensions.DependencyInjection;
using Relkit.Abstractions;
using Relkit.Client;
using Shouldly;
using Xunit;

namespace Relkit.Client.Tests;

public class AddRelkitClientTests
{
    [Fact]
    public void AddRelkitClient_registers_authorizer_and_managers_as_abstractions()
    {
        var services = new ServiceCollection();
        services.AddRelkitClient("https://localhost:5001");
        var provider = services.BuildServiceProvider();

        provider.GetService<IAuthorizer>().ShouldBeOfType<GrpcAuthorizer>();
        provider.GetService<IRelationManager>().ShouldBeOfType<GrpcRelationManager>();
        provider.GetService<ISchemaManager>().ShouldBeOfType<GrpcSchemaManager>();
        provider.GetService<IStoreManager>().ShouldBeOfType<GrpcStoreManager>();
        provider.GetService<ITenantManager>().ShouldBeOfType<GrpcTenantManager>();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Client.Tests --filter AddRelkitClientTests`
Expected: FAIL — `AddRelkitClient` does not exist.

- [ ] **Step 3: Implement the DI extension**

```csharp
// src/Relkit.Client/RelkitClientServiceCollectionExtensions.cs
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Relkit.Abstractions;
using G = Relkit.Grpc;

namespace Relkit.Client;

/// <summary>
/// Registers the Relkit gRPC client. A consumer swaps in-process evaluation (AddRelkit().UsePostgres())
/// for the remote service with a single call (spec §4, §10.1); all IAuthorizer/manager injections then
/// resolve to the remote client with no other code change.
/// </summary>
public static class RelkitClientServiceCollectionExtensions
{
    public static IServiceCollection AddRelkitClient(this IServiceCollection services, string address)
        => services.AddRelkitClient(new Uri(address));

    public static IServiceCollection AddRelkitClient(this IServiceCollection services, Uri address)
    {
        services.TryAddSingleton(_ => GrpcChannel.ForAddress(address));

        services.TryAddSingleton(sp => new G.Authorizer.AuthorizerClient(sp.GetRequiredService<GrpcChannel>()));
        services.TryAddSingleton(sp => new G.RelationManager.RelationManagerClient(sp.GetRequiredService<GrpcChannel>()));
        services.TryAddSingleton(sp => new G.SchemaManager.SchemaManagerClient(sp.GetRequiredService<GrpcChannel>()));
        services.TryAddSingleton(sp => new G.Provisioning.ProvisioningClient(sp.GetRequiredService<GrpcChannel>()));

        services.TryAddSingleton<IAuthorizer>(sp => new GrpcAuthorizer(sp.GetRequiredService<G.Authorizer.AuthorizerClient>()));
        services.TryAddSingleton<IRelationManager>(sp => new GrpcRelationManager(sp.GetRequiredService<G.RelationManager.RelationManagerClient>()));
        services.TryAddSingleton<ISchemaManager>(sp => new GrpcSchemaManager(sp.GetRequiredService<G.SchemaManager.SchemaManagerClient>()));
        services.TryAddSingleton<IStoreManager>(sp => new GrpcStoreManager(sp.GetRequiredService<G.Provisioning.ProvisioningClient>()));
        services.TryAddSingleton<ITenantManager>(sp => new GrpcTenantManager(sp.GetRequiredService<G.Provisioning.ProvisioningClient>()));

        return services;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Client.Tests --filter AddRelkitClientTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Client tests/Relkit.Client.Tests
git commit -m "feat: add AddRelkitClient DI extension for the gRPC swap"
```

---

### Task 7: End-to-end — client over the real service equals in-process, for the six worked examples

**Files:**
- Create: `tests/Relkit.Client.Tests/Relkit.Client.Tests.csproj` (add references)
- Create: `tests/Relkit.Client.Tests/ServiceFixture.cs`
- Test: `tests/Relkit.Client.Tests/ClientParityTests.cs`

**Interfaces:**
- Consumes: `Relkit.Service` (the host from `m3/01`) via `WebApplicationFactory<Program>`; `Grpc.Net.Client` over the factory's in-memory `HttpClient`; the `Relkit.Conformance` suite (`ConformanceSuite.All()`, `ConformanceCase`, from `m0/09`) for the six §12 worked examples; the in-process `EngineDrivenAuthorizer` for the oracle comparison.

> **The parity assertion (the point of this plan).** For each of the six worked examples, run the **same** decision two ways: (1) the in-process `EngineDrivenAuthorizer` over the in-memory provider (the `m0/09` `ConformanceRunner` already does this), and (2) `GrpcAuthorizer` against the running `Relkit.Service`, after seeding the same schema + tuples through the gRPC management facades. Assert the two `Allowed` results are identical, and that each equals the conformance case's declared `Expected`. This proves swapping to the remote service changes nothing the caller observes (spec §4).
>
> The service is configured to use the in-memory provider for this test (so the test needs no Postgres) — the host from `m3/01` registers stores via DI; the fixture overrides them with `Relkit.Storage.InMemory` so the parity test isolates the transport, not the storage. If `m3/01`'s `Program` does not expose an in-memory test seam, the fixture connects to a Testcontainers Postgres instead (note in Contract gaps).

- [ ] **Step 1: Add the test references**

Run:
```bash
dotnet add tests/Relkit.Client.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add tests/Relkit.Client.Tests reference src/Relkit.Service
dotnet add tests/Relkit.Client.Tests reference src/Relkit.Storage.InMemory
dotnet add tests/Relkit.Client.Tests reference tests/Relkit.Conformance
```

> `tests/Relkit.Conformance` is a test project; referencing it from another test project is allowed (it exposes `ConformanceSuite`/`WorkedExamples` as public types). If project-to-test references are undesirable in the build, move the worked-example case factory into a small shared `Relkit.Conformance.Cases` library — flagged in Contract gaps.

- [ ] **Step 2: Write the service fixture**

```csharp
// tests/Relkit.Client.Tests/ServiceFixture.cs
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Relkit.Abstractions;
using Relkit.Storage.InMemory;

namespace Relkit.Client.Tests;

/// <summary>
/// Hosts Relkit.Service in-memory via WebApplicationFactory and exposes a GrpcChannel over its test
/// HttpClient. The provider is overridden to the in-memory stores so parity tests isolate the transport.
/// </summary>
public sealed class ServiceFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace any registered storage with the shared in-memory provider so the test needs no DB.
            Replace<IRelationStore>(services, new InMemoryRelationStore());
            Replace<ISchemaStore>(services, new InMemorySchemaStore());
            Replace<IAttributeStore>(services, new InMemoryAttributeStore());
            Replace<ICacheStore>(services, new InMemoryCacheStore());
            Replace<IChangeLogStore>(services, new InMemoryChangeLogStore());
            services.AddSingleton<IUnitOfWorkFactory, NoOpUnitOfWorkFactory>();
        });
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        var existing = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in existing) services.Remove(d);
        services.AddSingleton(instance);
    }

    public GrpcChannel CreateChannel() =>
        GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpClient = CreateClient() });
}
```

> **In-memory store class names** (`InMemoryRelationStore`, `InMemorySchemaStore`, `InMemoryAttributeStore`, `InMemoryCacheStore`, `InMemoryChangeLogStore`, `NoOpUnitOfWorkFactory`) are from `m0/04`. The host's `Program` must be partial/public for `WebApplicationFactory<Program>` (owned by `m3/01`; add `public partial class Program;` to the host if missing — noted in Contract gaps).

- [ ] **Step 3: Write the parity test**

```csharp
// tests/Relkit.Client.Tests/ClientParityTests.cs
using Relkit.Abstractions;
using Relkit.Client;
using Relkit.Conformance;
using Relkit.Grpc;
using Shouldly;
using Xunit;

namespace Relkit.Client.Tests;

public class ClientParityTests : IClassFixture<ServiceFixture>
{
    private readonly ServiceFixture _fixture;
    public ClientParityTests(ServiceFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> Cases() =>
        ConformanceSuite.All().Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Remote_decision_matches_in_process_and_expected(ConformanceCase c)
    {
        // (1) In-process oracle: the m0/09 runner over the in-memory EngineDrivenAuthorizer.
        var inProcess = await ConformanceRunner.RunAsync(c);

        // (2) Remote: seed the same schema + tuples through the gRPC management facades, then Check.
        var channel = _fixture.CreateChannel();
        var tenant = new TenantContext("client-parity", c.Name);

        var stores = new GrpcStoreManager(new Provisioning.ProvisioningClient(channel));
        var tenants = new GrpcTenantManager(new Provisioning.ProvisioningClient(channel));
        var schemas = new GrpcSchemaManager(new SchemaManager.SchemaManagerClient(channel));
        var relations = new GrpcRelationManager(new RelationManager.RelationManagerClient(channel));
        var authorizer = new GrpcAuthorizer(new Authorizer.AuthorizerClient(channel));

        await stores.CreateStoreAsync(tenant.Store);
        await tenants.CreateTenantAsync(tenant);
        await tenants.CreateTenantAsync(new TenantContext(tenant.Store, tenant.Store)); // schema-audit bookkeeping
        await schemas.SetActiveSchemaAsync(tenant.Store, c.Schema);
        if (c.Tuples.Count > 0)
            await relations.WriteTuplesAsync(tenant, "parity-test", c.Tuples);
        foreach (var seed in c.Attributes)
            await relations.WriteAttributesAsync(tenant, "parity-test", seed.Object, seed.Attributes);

        var remote = await authorizer.CheckAsync(new CheckRequest(
            tenant, c.Object, c.Permission, c.Subject,
            new RequestContext(c.Now, c.Subject, c.Context)));

        // Parity: remote == in-process == the case's declared expectation.
        remote.Allowed.ShouldBe(inProcess.Allowed,
            $"remote/in-process divergence for '{c.Name}'");
        remote.Allowed.ShouldBe(c.Expected,
            $"remote decision wrong for '{c.Name}'");
    }
}
```

- [ ] **Step 4: Run the parity suite**

Run: `dotnet test tests/Relkit.Client.Tests --filter ClientParityTests`
Expected: PASS — one case per worked example, remote result equals in-process equals expected. A divergence here means the proto mapping or the host wiring lost information; diagnose `ProtoMapping`/`m3/01` before changing the engine.

- [ ] **Step 5: Run the whole client suite**

Run: `dotnet test tests/Relkit.Client.Tests`
Expected: PASS (wiring, mapping, status, authorizer, schema-json, DI, and parity tests).

- [ ] **Step 6: Commit**

```bash
git add tests/Relkit.Client.Tests
git commit -m "test: prove gRPC client parity with in-process for the six worked examples"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true` (generated proto warnings scoped, not blanket-suppressed).
- [ ] `GrpcAuthorizer : IAuthorizer` implements all four ops; a consumer swaps in-process for remote with one `AddRelkitClient(address)` call and no caller change (spec §4, §10.1).
- [ ] `ProtoMapping` round-trips every record (`ToDomain(ToProto(x)) == x`), including the subject-set marker, wildcard, optional condition, and typed attribute/param values.
- [ ] Typed exceptions (`UnknownType/Relation/Permission`, `SchemaValidation`, `EvaluationLimit`) re-throw client-side via `RemoteStatus`, so deny-vs-error (spec §10.3) survives the wire.
- [ ] The four management facades cover `IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager`; `SchemaJson` round-trips the polymorphic AST.
- [ ] The parity test asserts remote == in-process == expected for all six §12 worked examples, against the real `Relkit.Service` over `WebApplicationFactory` + `Grpc.Net.Client`.

## Contract gaps (reported, not changed)

- **Proto ownership (`m3/01`).** This plan reproduces `relkit.proto` (messages, services, the `relkit-error-*` trailer keys) so the mapping is unambiguous, but `m3/01` owns the canonical file. The client and host must compile the **same** proto. If `m3/01` names a field, RPC, `google.protobuf.Struct`-vs-bytes choice, or error trailer differently, `ProtoMapping`/`RemoteStatus` adjust to match `m3/01`; no engine contract changes. Recommended: `m3/01` keeps the shared `.proto` at `proto/relkit.proto` and both projects `<Protobuf Include>` it.
- **Shared schema serializer.** `SchemaJson` here carries its own `System.Text.Json` polymorphic resolver with `$kind` discriminators for `PermExpr`/`ConditionExpr`. The host (`m3/01`/`m1/01`) must serialize schemas with the **identical** discriminators or the jsonb/JSON round-trip breaks across the wire. The clean fix is a single `SchemaSerializer` in `Relkit.Core` (or `Relkit.Abstractions`) that both the host and this client call. Flagged for the contract owner to add a canonical serializer; until then the discriminator strings in `SchemaJson` and the host must be kept in lockstep.
- **`Relkit.Service` test seam.** The parity fixture assumes the host's `Program` is reachable as `WebApplicationFactory<Program>` and lets the test override storage with the in-memory provider. `m3/01` should expose `public partial class Program;` and register stores via swappable DI so tests can inject `Relkit.Storage.InMemory`. If not, the fixture falls back to a Testcontainers Postgres-backed host. Flagged for `m3/01`.
- **Conformance reuse.** This plan references `tests/Relkit.Conformance` to reuse `ConformanceSuite.All()`. If a test-project-to-test-project reference is undesirable, extract the worked-example case factory into a small shared library (`Relkit.Conformance.Cases`) that both the conformance suite and this parity test reference. Flagged; not changed here.
