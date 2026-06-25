# M3/04 — Custodex gRPC Client

**Goal:** A `Custodex.Client` package: a gRPC client implementing the canonical `Custodex.Abstractions.IAuthorizer` over the network, so a consumer swaps in-process evaluation for the remote `Custodex.Service` by changing **one** DI registration; plus thin gRPC clients for the four management interfaces; and an `AddCustodexClient(address)` extension. The client is proven against the real `Custodex.Service` host (via `WebApplicationFactory` + `Grpc.Net.Client`) by asserting it returns the **identical** decisions the in-process `EngineDrivenAuthorizer` returns for the six spec §12 worked examples.

**For implementers:** drive this plan with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each task is TDD — Red → Green → Commit — tracked by its `- [ ]` checkbox. One Conventional Commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture:** `Custodex.Client` references `Custodex.Abstractions` (for the `IAuthorizer`/manager interfaces and the canonical records) and compiles the gRPC contract owned by `m3/01` (`Grpc.Tools`, `GrpcServices="Client"`). `GrpcAuthorizer : IAuthorizer` holds a generated authorizer client, maps canonical records to proto, calls the service, restores typed exceptions, and maps results back — callers see only `Custodex.Abstractions`. A `ProtoMapping` static class owns every record ⇄ proto conversion in one place, shared by the management facades. `AddCustodexClient(address)` registers the channel, the generated clients, and the five facades against their interfaces, so a consumer who had `AddCustodex().UsePostgres(...)` instead calls `AddCustodexClient("https://...")` with no other code change (spec §4, §10.1).

> **Proto ownership.** `m3/01` owns the canonical `.proto`; the client compiles the **same** file (copied or linked) so host and client never drift. This plan's contract surface is described against that file. The `m3/01` host splits the contract into common/decision/management protos under namespace `Custodex.Service.Grpc`; this client treats the contract as one logical surface (namespace `Custodex.Grpc`) and uses explicit `has_condition`/`has_explain` presence fields. If the host's field names or presence convention differ, `ProtoMapping`/`RemoteStatus` adjust to match the shared file — no engine change.

**Tech stack:** .NET 10 / C# 14, xUnit + Shouldly, `Grpc.Net.Client`, `Grpc.Net.ClientFactory`, `Google.Protobuf`, `Grpc.Tools`, `Microsoft.AspNetCore.Mvc.Testing`.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies (see README):** the `Custodex.Abstractions` contract; the proto + `Custodex.Service` host (`m3/01`); the in-process `EngineDrivenAuthorizer` and the conformance suite's six worked examples for the parity oracle.

## Shared decisions

- **The proto is owned by `m3/01`;** the client compiles the same file. `ProtoMapping` is adjusted to the shared file if names differ.
- **`ProtoMapping` is the one marshalling seam.** Every `EntityRef`/`SubjectRef`/`RelationTuple`/`RequestContext`/condition conversion lives there; the facades and `GrpcAuthorizer` call it.
- **Deny vs error preserved over the wire (spec §10.3).** Allow/deny is a normal boolean; the typed exceptions are carried as gRPC `Status` with structured trailers and **re-thrown** client-side as the same exception type.
- **`object?` params/attributes** marshal through `google.protobuf.Struct`/`Value`, so int/long/double/bool/string/timestamp round-trip; `ProtoMapping` centralizes the boxing rules.

---

### Task 1: Create `Custodex.Client`, reference Abstractions, compile the shared proto

- [ ] **Files:** add `Custodex.Client.csproj`, `Protos/Custodex.proto` (the file `m3/01` owns, copied or linked, `GrpcServices="Client"`); add the `Custodex.Client.Tests` project; test `…Tests/ClientWiringTests.cs`.

**Produces:** the `Custodex.Client` assembly referencing `Custodex.Abstractions` and the generated client stubs (`Authorizer.AuthorizerClient`, `RelationManager.RelationManagerClient`, `SchemaManager.SchemaManagerClient`, `Provisioning.ProvisioningClient`).
**Consumes (see README):** `Custodex.Abstractions`; the `m3/01` proto contract.

**Behavior:** the proto declares the shared messages (`EntityRef`, `SubjectRef` with empty-relation ⇒ not-a-subject-set, `ConditionRef`, `RelationTuple` with a `has_condition` presence flag, `TenantContext`, `RequestContext`, `ExplainNode`) and the four services — `Authorizer` (Check/BatchCheck/ListObjects/ListSubjects), `RelationManager`, `SchemaManager`, `Provisioning`. Generated proto code that trips `TreatWarningsAsErrors` is suppressed only for the generated files (scoped `NoWarn`), never blanket.

**Cases to pin:**

| Setup | Expect |
|---|---|
| reference the four generated `*Client` types | all exist after the first build runs codegen |

**Done when:** build clean (scoped generated-code warnings only); the four client stubs are generated.

---

### Task 2: `ProtoMapping` — record ⇄ proto marshalling

- [ ] **Files:** add `ProtoMapping.cs`; test `…Tests/ProtoMappingTests.cs`.

**Produces:** `static class ProtoMapping` with `ToProto`/`ToDomain` for `EntityRef`, `SubjectRef`, `RelationTuple`, `RequestContext`, `TenantContext`, `ExplainNode`, `TupleFilter`/`ChangeLogFilter`/`ChangeLogEntry`, and the `Struct ⇄ IReadOnlyDictionary<string, object?>` helpers (`AttributesToProto`/`AttributesToDomain`).
**Consumes (see README):** the canonical records; the generated `Custodex.Grpc.*` messages; `Google.Protobuf.WellKnownTypes`.

**Behavior:** round-trip is the invariant — `ToDomain(ToProto(x)) == x`. The subject-set marker is `Relation is null` ⇄ proto `relation == ""`; the wildcard id `"*"` survives untouched; an optional condition is domain `null` ⇄ proto `has_condition == false`. Attribute/param boxing: bool → bool; string → string; int/long/double/float → number; `DateTimeOffset`/`DateTime` → ISO-8601 string. On decode, an integral `Value` number returns `long` (the evaluator widens to double when needed). Dictionaries decode with ordinal key comparison.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `EntityRef` plain + wildcard | round-trip equal |
| `SubjectRef` subject-set + plain | round-trip equal; `IsSubjectSet` correct |
| tuple with a condition | round-trips with typed params (`long`) |
| unconditioned tuple | `Condition` null |
| attribute Struct {long, string, bool, double} | typed values round-trip |
| `RequestContext` | `Now` + subject round-trip |

**Done when:** build clean; cases pass; every record round-trips, including subject-set marker, wildcard, optional condition, and typed values.

---

### Task 3: `RemoteStatus` — typed-exception preservation over the wire

- [ ] **Files:** add `RemoteStatus.cs`; test `…Tests/RemoteStatusTests.cs`.

**Produces:** `static class RemoteStatus` with `T Unwrap<T>(Func<T>)` and `Task<T> UnwrapAsync<T>(Func<Task<T>>)` that catch `RpcException`, read structured trailers, and re-throw the matching `Custodex.Abstractions` exception — otherwise rethrow the `RpcException` untouched.
**Consumes (see README):** `Grpc.Core.RpcException`/`StatusCode`/`Metadata`; the engine exception hierarchy.

**Behavior:** the service maps each typed exception to a gRPC status (caller-bug ⇒ `InvalidArgument`, schema ⇒ `FailedPrecondition`) and stamps a `Custodex-error-kind` trailer plus detail trailers. The client reconstructs the exact exception so a remote caller sees what a local caller would (spec §10.3). The trailer scheme (owned by the `m3/01` host; this client matches the shared file):

| `Custodex-error-kind` | reconstructed exception (from detail trailers) |
|---|---|
| `unknown_type` | `UnknownTypeException` (`Custodex-error-type`) |
| `unknown_relation` | `UnknownRelationException` (type, relation) |
| `unknown_permission` | `UnknownPermissionException` (type, permission) |
| `schema_invalid` | `SchemaValidationException` (repeated `Custodex-error-message`) |
| `evaluation_limit` | `EvaluationLimitException` (`Custodex-error-detail`) |
| (none / unrecognized) | the original `RpcException` |

**Cases to pin:**

| Setup | Expect |
|---|---|
| `InvalidArgument` + `unknown_type` trailer | `UnknownTypeException` with the type |
| `unknown_permission` trailers | `UnknownPermissionException` with type + permission |
| `FailedPrecondition` + repeated `schema_invalid` messages | `SchemaValidationException` with both errors |
| `Unavailable` with no Custodex trailer | the `RpcException` rethrown as-is |

**Done when:** build clean; cases pass; typed exceptions reconstruct and transport failures pass through.

---

### Task 4: `GrpcAuthorizer : IAuthorizer`

- [ ] **Files:** add `GrpcAuthorizer.cs`; test `…Tests/GrpcAuthorizerMappingTests.cs` (request shaping asserted via a fake client; full parity in Task 7).

**Produces:** `GrpcAuthorizer(Authorizer.AuthorizerClient) : IAuthorizer` — all four ops, each mapping the canonical request to proto, calling the stub, unwrapping typed errors via `RemoteStatus`, and mapping the response back.
**Consumes (see README):** the generated authorizer client; `ProtoMapping`; `RemoteStatus`; the `IAuthorizer` contract.

**Behavior:** the constructor takes the generated client (not a channel) so a unit test can inject a fake. `ListObjectsRequest.ContinuationToken == null` maps to proto `""`; a proto `""` response token maps back to `null` (no more pages). Each op flows request → proto → `client.*Async` → response → records, wrapped in `RemoteStatus.UnwrapAsync`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `CheckAsync` against a fake | request fields shaped correctly; `allowed` returned |
| `ListObjectsAsync` with null token | proto token `""` sent; `""` response token decodes to null |

**Done when:** build clean; cases pass; all four ops shape requests and decode responses correctly.

---

### Task 5: Management client facades + `SchemaJson`

- [ ] **Files:** add `SchemaJson.cs`, `GrpcRelationManager.cs`, `GrpcSchemaManager.cs`, `GrpcProvisioning.cs` (`GrpcStoreManager` + `GrpcTenantManager`); reference `Custodex.Core` for the AST + builder used in tests; test `…Tests/SchemaJsonTests.cs`.

**Produces:**
- `static class SchemaJson` — `Serialize(Schema)` / `Deserialize(string)` via `System.Text.Json` with `$kind` polymorphism over the `PermExpr` and `ConditionExpr` hierarchies, round-tripping the canonical AST.
- `GrpcRelationManager : IRelationManager`, `GrpcSchemaManager : ISchemaManager`, `GrpcStoreManager : IStoreManager`, `GrpcTenantManager : ITenantManager`, each over its generated client.
**Consumes (see README):** `ProtoMapping`, `RemoteStatus`, the generated stubs, the manager contracts, the `Schema`/`PermExpr`/`ConditionExpr` AST.

**Behavior:** `SchemaJson` must serialize with the **identical** discriminators the host uses, or the wire round-trip breaks; `ISchemaManager.ValidateSchema` is synchronous, so its gRPC call is blocking-wrapped while `SetActive`/`GetActive` are async. The facades translate via `ProtoMapping` and wrap calls in `RemoteStatus`; `GetActiveSchemaAsync` returns `null` when the service reports no schema.

> **Prefer a shared serializer.** If the engine exposes a canonical `SchemaSerializer` in `Custodex.Core`, `SchemaJson` forwards to it instead of carrying its own discriminator set — one serializer, guaranteed to match the host. (Surfaced in the report.)

**Cases to pin:**

| Setup | Expect |
|---|---|
| a schema with `Exclude(Union(RelationRef, Arrow), …)` algebra | `Deserialize(Serialize(s))` structurally equals `s` |

**Done when:** build clean; `SchemaJson` round-trips the polymorphic AST; the four facades compile (exercised end-to-end in Task 7).

---

### Task 6: `AddCustodexClient(address)` DI extension

- [ ] **Files:** add `CustodexClientServiceCollectionExtensions.cs`; test `…Tests/AddCustodexClientTests.cs`.

**Produces:** `AddCustodexClient(this IServiceCollection, string address)` (+ a `Uri` overload) registering a single `GrpcChannel`, the four generated clients, and `GrpcAuthorizer`/`GrpcRelationManager`/`GrpcSchemaManager`/`GrpcStoreManager`/`GrpcTenantManager` against `IAuthorizer`/the four manager interfaces.
**Consumes (see README):** `Grpc.Net.Client.GrpcChannel`; `Microsoft.Extensions.DependencyInjection`.

**Behavior:** the one-line swap (spec §10.1) — a consumer who used `AddCustodex().UsePostgres(...)` replaces it with `AddCustodexClient("https://...")` and every `IAuthorizer`/manager injection resolves to the remote client, unchanged callers, because both register the same `Custodex.Abstractions` interfaces.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `AddCustodexClient(address)` then resolve each interface | `IAuthorizer`→`GrpcAuthorizer`, and each manager → its `Grpc*` facade |

**Done when:** build clean; the case passes; all five interfaces resolve to the remote facades.

---

### Task 7: End-to-end parity — client over the real service equals in-process

- [ ] **Files:** add test references (`Custodex.Service`, `Custodex.Storage.InMemory`, the conformance suite); add `ServiceFixture.cs`; test `…Tests/ClientParityTests.cs`.

**Produces:** the parity test (the point of this plan).
**Consumes (see README):** `Custodex.Service` via `WebApplicationFactory<Program>`; `Grpc.Net.Client` over the factory's in-memory handler; the conformance suite's six §12 worked examples; the in-process `EngineDrivenAuthorizer` oracle.

**Behavior:** for each worked example, run the **same** decision two ways — (1) the in-process oracle over the in-memory provider (the conformance runner), and (2) `GrpcAuthorizer` against the running `Custodex.Service` after seeding the same schema + tuples through the gRPC management facades. Assert `remote.Allowed == in-process.Allowed == case.Expected`. This proves swapping to the remote service changes nothing the caller observes (spec §4). The `ServiceFixture` hosts `Custodex.Service` and overrides its storage with the in-memory provider so the parity test isolates the transport, not the database; if the host exposes no in-memory test seam, the fixture falls back to a Testcontainers Postgres-backed host.

**Cases to pin:**

| Setup | Expect |
|---|---|
| each of the six worked examples, seeded remotely | `remote == in-process == expected` |

**Done when:** build clean; one case per worked example passes; a divergence implicates `ProtoMapping`/host wiring, not the engine.

---

## Self-review checklist

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true` (generated proto warnings scoped, not blanket-suppressed).
- [ ] `GrpcAuthorizer : IAuthorizer` implements all four ops; the swap is one `AddCustodexClient(address)` call with no caller change (spec §4, §10.1).
- [ ] `ProtoMapping` round-trips every record (`ToDomain(ToProto(x)) == x`), including subject-set marker, wildcard, optional condition, and typed values.
- [ ] Typed exceptions re-throw client-side via `RemoteStatus`, so deny-vs-error (spec §10.3) survives the wire.
- [ ] The four management facades cover the manager interfaces; `SchemaJson` round-trips the polymorphic AST.
- [ ] The parity test asserts remote == in-process == expected for all six §12 worked examples against the real host.
