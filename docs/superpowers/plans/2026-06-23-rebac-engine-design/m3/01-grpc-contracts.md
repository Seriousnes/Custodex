# M3/01 — gRPC Contracts & Service Host

**Goal:** Stand up `Custodex.Service` — the ASP.NET (`net10.0`) host that exposes the engine over gRPC. Define `.proto` contracts that mirror `IAuthorizer` (Check/BatchCheck/ListObjects/ListSubjects) and the management surface, and implement gRPC services that delegate to the **same** in-process `Custodex.Core` + Postgres engine the in-process consumer uses.

**For implementers:** drive this plan with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each task is TDD — Red (failing test) → Green (minimal implementation) → Commit — and tracked by its `- [ ]` checkbox. One Conventional Commit per green task, with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture** (spec §4, §10.1): `Custodex.Service` composes the engine exactly as the in-process consumer does — `AddCustodex().UsePostgres(conn)` (with `AddCustodex()` from `Custodex.Core`, `.UsePostgres()` from `Custodex.Storage.Postgres`). The gRPC layer is a thin translation boundary: each service depends only on the public `Custodex.Abstractions` interfaces (`IAuthorizer`, `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager`) resolved from DI, converts proto ⇄ contract records, and adds **zero** new evaluation semantics. A shared `ProtoMap` owns every record ⇄ message conversion so the mapping is defined and tested once. Heterogeneous `RequestContext.Attributes` and `ConditionRef.Parameters` (`IReadOnlyDictionary<string, object?>`) map to `google.protobuf.Struct` so values round-trip without a bespoke variant type.

**Tech stack:** .NET 10 / C# 14, ASP.NET Core, `Grpc.AspNetCore` (+ transitive `Grpc.Tools`, `Google.Protobuf`), the engine packages, xUnit + Shouldly, `Grpc.Net.Client`, `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`), `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints. No EF Core; tenant isolation on every operation.

**Dependencies (see README):** the `Custodex.Abstractions` contract (every type name below is verbatim from it); `AddCustodex()`/`.UsePostgres()`/`AddCustodexInstrumentation()`, the concrete managers, and `MigrationRunner`. Per `../README.md` → Aspire integration, `Custodex.Service`/`Custodex.AppHost`/`Custodex.ServiceDefaults` **already exist** — do not scaffold them; build on `AddServiceDefaults()` / `MapDefaultEndpoints()` and enable `AddGrpcClientInstrumentation()` in ServiceDefaults tracing (the full ServiceDefaults + telemetry wiring is `m3/07`).

## Shared decisions

- **The service is a thin front door.** gRPC services hold only the `Custodex.Abstractions` interfaces; all engine behaviour comes from `AddCustodex().UsePostgres()`. No evaluation, validation, or persistence logic lives in `Custodex.Service`.
- **One `Custodex.v1` proto package, four service definitions.** `Decision` (the four read ops), `Relations`, `Schema`, and `Provisioning`. Splitting management from decisions lets `m3/03` apply distinct authorization policies per service.
- **Heterogeneous values use `google.protobuf.Struct`.** `ProtoMap` centralises `object? ⇄ Value` (int/long → number, double → number, bool → bool, string → string, null → null value).
- **Tenancy is explicit in the proto here.** Every request message carries `store` + `tenant` building a `TenantContext`; `m3/03` moves resolution to headers/claims. Auth is deferred to `m3/03` — this plan leaves the endpoints open and tests over `WebApplicationFactory` against Testcontainers Postgres.

---

### Task 1: `Custodex.Service` host wired to the Postgres engine

- [ ] **Files:** add `Program.cs`, `appsettings.json` to `src/Custodex.Service`; add `PostgresFixture.cs` (one Testcontainers Postgres shared by the `"service"` collection, running `MigrationRunner.ApplyAsync`) to `tests/Custodex.Service.Tests`; test `…Tests/HostBootTests.cs`.

**Produces:** a runnable host that calls `AddCustodex().UsePostgres(conn)`, reads `Custodex:ConnectionString` from config, maps a health endpoint, and exposes `public partial class Program;` so `WebApplicationFactory<Program>` can host it in tests.
**Consumes (see README):** `AddCustodex()`/`.UsePostgres()`/`AddCustodexInstrumentation()`; `SchemaBuilder`; `MigrationRunner`.

**Behavior:** the host composes the identical `Custodex.Core` + Postgres engine the in-process consumer wires. A startup schema may be supplied; absent one, schemas are managed at runtime via `ISchemaManager`. OTel registration goes through `AddCustodexInstrumentation()` on the tracing and metrics builders.

**Cases to pin:**

| Setup | Expect |
|---|---|
| GET `/health` | 200, body contains `ok` |
| resolve `IAuthorizer` from the host scope | non-null |

**Done when:** build clean; cases pass; the host boots over Testcontainers Postgres and resolves `IAuthorizer`.

---

### Task 2: The decision `.proto` and generated stubs

- [ ] **Files:** add `Protos/Custodex_common.proto` and `Protos/Custodex_decision.proto`; register `<Protobuf>` items in `src/Custodex.Service/Custodex.Service.csproj`; test `…Tests/ProtoCompileTests.cs`.

**Produces:** the `Custodex.v1` package with shared messages and the `Decision` service. `Grpc.Tools` generates the C# server stubs at build.
**Consumes (see README):** the contract records the messages mirror — `EntityRef`, `SubjectRef`, `ConditionRef`, `RequestContext`, `ExplainNode`, `RelationTuple`; `google/protobuf/struct.proto` + `timestamp.proto`.

**Behavior:** each message mirrors its record field-for-field. `EntityRef{type,id}`; `SubjectRef{type,id,relation}` where an **empty** `relation` means "not a subject-set" (maps to `SubjectRef.Relation == null`); `ConditionRef{name, parameters:Struct}`; `RequestContext{now:Timestamp, subject, attributes:Struct}`; `ExplainNode{description, allowed, repeated children}` (recursive). `TenantContext{store, tenant}` carries tenancy explicitly here. `Custodex_common.proto` generates message types only (`GrpcServices="None"`); the decision proto imports it and generates the server base classes (`GrpcServices="Server"`).

The `Decision` service:

| rpc | request → response |
|---|---|
| `Check` | `CheckRequest{tenant, object, permission, subject, context, explain}` → `CheckResponse{allowed, explain}` (explain present only when requested) |
| `BatchCheck` | `BatchCheckRequest{tenant, repeated CheckItem{object, permission, subject}, context}` → `BatchCheckResponse{repeated CheckResponse}` (positionally aligned) |
| `ListObjects` | `ListObjectsRequest{tenant, subject, object_type, permission, context, page_size, continuation_token}` → `ListObjectsResponse{repeated object_ids, continuation_token}` |
| `ListSubjects` | `ListSubjectsRequest{tenant, object, permission, context, page_size, continuation_token}` → `ListSubjectsResponse{repeated SubjectRef subjects, continuation_token}` |

An empty `continuation_token` means first page (request) / no further pages (response).

**Cases to pin:**

| Setup | Expect |
|---|---|
| construct a generated `CheckRequest` and read its fields | fields round-trip in memory |
| reference `Decision.DecisionBase` | generated type exists |

**Done when:** build clean; protos compile; the decision server base class is generated.

---

### Task 3: `ProtoMap` — record ⇄ message conversion

- [ ] **Files:** add `Mapping/ProtoMap.cs`; test `…Tests/ProtoMapTests.cs`.

**Produces:** the `ProtoMap` static class — bidirectional converters for every shared type (`To/From` `EntityRef`, `SubjectRef`, `ConditionRef`, `RequestContext`; `ToTenantContext`; `FromExplainNode`) plus the `Struct ⇄ IReadOnlyDictionary<string, object?>` helpers `ToStruct`/`FromStruct`.
**Consumes (see README):** the shared `Custodex.Abstractions` records; `Google.Protobuf.WellKnownTypes` (`Struct`, `Value`, `Timestamp`).

**Behavior:**
- **The null-`Relation` rule is the subtle one.** proto3 strings cannot be null, so an **empty** `relation` maps to `null` (plain subject) and any non-empty value maps through (subject-set). `IsSubjectSet` therefore stays correct across the wire.
- **Numeric fidelity.** `Struct` has a single numeric kind (double), so integer attributes/parameters arrive as `double` after a round-trip. The condition evaluator already coerces numerics for comparisons, so this is faithful for the engine; tests assert via `Convert.ToInt32`/`ToDouble` to document the coercion.
- A `RequestContext` with no `now`/`subject` decodes to a defaulted `Now` and a wildcard subject rather than throwing.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `EntityRef("user","*")` round-trip | equal, wildcard preserved |
| proto `SubjectRef` with empty relation | `Relation` null, `IsSubjectSet` false |
| `SubjectRef("group","vets","member")` round-trip | equal, relation `member` |
| Struct round-trip of {long, double, bool, string, null} | numerics coerce, bool/string/null exact |
| `ConditionRef` with int parameters round-trip | name + parameters preserved |
| `RequestContext{now, subject, attributes}` | now/subject/attributes mapped |
| `ExplainNode` with two children | recursive shape + per-node `allowed` preserved |

**Done when:** build clean; cases pass; every shared type round-trips, including the empty-relation ⇒ null rule and the Struct coercion.

---

### Task 4: `DecisionGrpcService` — the four read ops over gRPC

- [ ] **Files:** add `Services/DecisionGrpcService.cs`; map it in `Program.cs`; test `…Tests/DecisionServiceTests.cs`.

**Produces:** `DecisionGrpcService : Decision.DecisionBase`, delegating each RPC to `IAuthorizer` via `ProtoMap`, mapped by `app.MapGrpcService<DecisionGrpcService>()`.
**Consumes (see README):** `IAuthorizer` and its request/result records; `ProtoMap` (Task 3); the generated `Decision.DecisionBase` (Task 2).

**Behavior:** delegation only. Build the canonical request from the proto message, call the resolved `IAuthorizer`, map the result back. The same `IAuthorizer` (the Postgres CTE authorizer registered by `.UsePostgres()`) serves both this front door and the in-process consumer. A non-positive `page_size` defaults to a sensible page; an empty `continuation_token` maps to `null`; an absent `Explain` result leaves the response `explain` unset. The decision test seeds a schema + tuples through the manager interfaces over the host scope, then exercises the gRPC client.

**Cases to pin:**

| Setup | Expect |
|---|---|
| Check, subject granted via a group grant | `allowed` true |
| Check, ungranted subject | `allowed` false |
| ListObjects for a subject with two grants | both object ids, sorted |
| BatchCheck of {granted, ungranted} | two results, aligned, true then false |

**Done when:** build clean; cases pass; all four ops answer through `IAuthorizer` over `WebApplicationFactory` + Testcontainers Postgres.

---

### Task 5: The management `.proto` and generated stubs

- [ ] **Files:** add `Protos/Custodex_management.proto`; register it in the csproj (`GrpcServices="Server"`); test `…Tests/ManagementProtoCompileTests.cs`.

**Produces:** the `Relations`, `Schema`, and `Provisioning` services in `Custodex.v1`, plus their messages including a `RelationTuple` message (object + relation + subject + optional condition) mirroring the record.
**Consumes (see README):** `Custodex_common.proto`; the `TupleFilter`/`ChangeLogFilter`/`ChangeLogEntry` and manager contracts the messages mirror.

**Behavior:** schema travels over the wire as a `schema_json` string (the canonical-model serialization — see `SchemaJson`, Task 6); `m3/05`'s DSL adds a text form later. The services:

| service | rpcs (request → response intent) |
|---|---|
| `Relations` | `WriteTuples` / `DeleteTuples` (`tenant, actor, repeated RelationTuple` → count); `WriteAttributes` (`tenant, actor, object, attributes:Struct`); `ReadTuples` (filter columns, empty ⇒ no constraint → tuples); `ReadChangeLog` (`since?, actor?, limit` → `repeated ChangeLogEntry`) |
| `Schema` | `Validate` (`schema_json` → `is_valid` + `repeated errors`); `SetActive` (`store, schema_json`); `GetActive` (`store` → `found` + `schema_json`) |
| `Provisioning` | `CreateStore` (`store`); `CreateTenant` (`tenant`) |

`ChangeLogEntry` carries `id, actor, operation, target, before_json, after_json, occurred_at` — the `before`/`after` images serialize as JSON strings (empty when null).

**Cases to pin:**

| Setup | Expect |
|---|---|
| reference `Relations.RelationsBase` / `Schema.SchemaBase` / `Provisioning.ProvisioningBase` | all generated |
| construct a `RelationTuple` message with a subject-set | object/relation/subject fields round-trip |

**Done when:** build clean; the three management server base classes generate; `RelationTuple` carries object/relation/subject/condition.

---

### Task 6: Management service implementations + schema JSON mapping

- [ ] **Files:** add `Mapping/SchemaJson.cs`, `Services/RelationsGrpcService.cs`, `Services/SchemaGrpcService.cs`, `Services/ProvisioningGrpcService.cs`; map the three services in `Program.cs`; test `…Tests/ManagementServiceTests.cs`.

**Produces:**
- `SchemaJson` — `Serialize(Schema)` / `Deserialize(string)` via `System.Text.Json`, registering a `$kind` polymorphic discriminator set over the `PermExpr` hierarchy (`RelationRef`/`Union`/`Intersect`/`Exclude`/`Arrow`/`Conditioned`) and over `ConditionExpr` so the sealed AST subtypes round-trip to/from jsonb.
- `RelationsGrpcService : Relations.RelationsBase` over `IRelationManager`; `SchemaGrpcService : Schema.SchemaBase` over `ISchemaManager`; `ProvisioningGrpcService : Provisioning.ProvisioningBase` over `IStoreManager`/`ITenantManager`.
**Consumes (see README):** the four manager interfaces; `ProtoMap`; the generated management stubs (Task 5); the `Schema`/`PermExpr`/`ConditionExpr` AST.

**Behavior:** the management services translate proto ⇄ records and call the manager interface. `Validate`/`SetActive` exercise the schema manager, which throws `SchemaValidationException` on an invalid schema — mapped to a gRPC `InvalidArgument` status. `ReadTuples` builds a `TupleFilter` where empty proto string fields mean "no constraint"; `ReadChangeLog` defaults a non-positive `limit`. `SchemaJson` is the canonical-model transport until the DSL lands; it is also reused by the REST surface (`m3/02`).

**Cases to pin:**

| Setup | Expect |
|---|---|
| provision store + tenants → SetActive schema → GetActive | `found` true |
| write a conditioned/subject-set tuple → ReadTuples by object type | the tuple comes back with its subject relation |
| ReadChangeLog after a write | an entry with that actor and `write` operation |
| SetActive with a permission referencing a missing relation | `RpcException` with `InvalidArgument` |

**Done when:** build clean; cases pass; provisioning → schema activation → audited tuple write → read-back round-trips over gRPC; invalid schema surfaces as `InvalidArgument`.

---

### Task 7: Startup migrations and final host wiring

- [ ] **Files:** modify `Program.cs` (apply migrations at startup, map all four gRPC services); test `…Tests/StartupMigrationTests.cs` over a clean container with no pre-applied schema.

**Produces:** a host that runs the idempotent `MigrationRunner.ApplyAsync` at startup (guarded by `Custodex:ApplyMigrationsOnStartup`, default true) so a fresh database is usable on first boot, with all four gRPC services mapped.
**Consumes (see README):** `MigrationRunner`.

**Behavior:** migrations are idempotent and safe to re-run, so applying them at startup makes a freshly provisioned container ready without a separate step; a consumer who manages migrations externally disables this via configuration.

**Cases to pin:**

| Setup | Expect |
|---|---|
| boot against a clean container, GET `/health` | 200 |
| provision a store after that boot | succeeds (tables exist) |

**Done when:** build clean; the full `Custodex.Service.Tests` suite is green over Testcontainers Postgres.

---

## Self-review checklist

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [ ] The service composes the engine via `AddCustodex().UsePostgres()` exactly as the in-process consumer; no evaluation/persistence logic in the service.
- [ ] `Custodex.v1` declares `Decision`, `Relations`, `Schema`, `Provisioning`, with messages mirroring the contract records.
- [ ] `ProtoMap` round-trips every shared type, including the empty-relation ⇒ null rule and the `Struct ⇄ object?` coercion.
- [ ] `DecisionGrpcService` answers all four ops through `IAuthorizer`; the management services provision/validate/activate/audit through the manager interfaces (invalid schema ⇒ `InvalidArgument`).
- [ ] Migrations apply on startup; the full service test suite is green.
