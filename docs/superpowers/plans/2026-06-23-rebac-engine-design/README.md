# Custodex Implementation Plans — Foundation

> This is the **shared contract** for every plan under this directory. Each milestone plan (`m0/`–`m3/`) is written against the constraints, namespaces, interfaces, and type names defined here. When a plan needs a type, it uses the exact name and signature below. Changes to this contract are made here first, then propagated — see the maintenance note at the end.

**Source spec:** `docs/superpowers/specs/2026-06-23-rebac-engine-design.md` (read it before any plan).

**Goal:** A runtime-configurable ReBAC + ABAC authorization engine, shipped as a reusable .NET library and later a standalone service.

**Architecture:** Generic engine (`Custodex.Core`) over storage interfaces (`Custodex.Abstractions`), first implemented for Postgres (`Custodex.Storage.Postgres`). Tenant configuration is data (tuples + conditions) against a per-app schema. Evaluation is engine-driven traversal (the portable path and correctness oracle) with a Postgres recursive-CTE primary path; a maintained reverse index accelerates list operations.

---

## Global Constraints

Every task implicitly includes these.

- **Target framework:** `net10.0` (all library projects and tests).
- **Language:** C# 14, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- **Test framework:** xUnit. **Assertions:** Shouldly. **Property tests:** CsCheck (MIT). **Postgres integration tests:** Testcontainers (`Testcontainers.PostgreSql`). **Benchmarks:** BenchmarkDotNet.
- **Postgres data access:** Dapper + Npgsql. **No EF Core dependency** in any shipped package.
- **License:** Apache-2.0. Every package sets `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>`. No dependency under a non-permissive or paid license (explicitly: not FluentAssertions v8+).
- **Async:** all I/O methods are `async` and take a trailing `CancellationToken ct = default`.
- **Identifiers** (`store`, `tenant`, `type`, `id`, `relation`, `permission`, `condition name`) are `string`, compared ordinal, non-empty. The reserved object/subject id `"*"` denotes a wildcard.
- **Determinism:** no `DateTime.Now`/`Guid.NewGuid()` inside evaluation; ambient time enters only through `RequestContext.Now`.
- **Commits:** Conventional Commits (`feat:`, `test:`, `refactor:`, `chore:`). Commit after every green test step. Co-author trailer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

## Repository & solution layout

The solution is **.NET Aspire**-based and **already scaffolded** (`Custodex.slnx` + empty project shells exist). Plans **do not run `dotnet new sln`/`classlib`**; they add packages, references, and source to the existing projects.

Projects marked **(NEW)** do not exist in `Custodex.slnx` yet; the plan that introduces one **creates it and adds it to the solution**. All others already exist.

```
Custodex.slnx                 # solution (XML .slnx format — already exists)
src/
  Custodex.Abstractions/      # contracts only — no logic
  Custodex.Core/              # engine: schema, evaluation, conditions, caching, DSL (Custodex.Core.Dsl), AddCustodex() builder
  Custodex.Storage.InMemory/  # (NEW, m0/04) in-memory provider for tests/dev
  Custodex.Storage.Postgres/  # Dapper/Npgsql provider + UsePostgres() + managers (M1+)
  Custodex.Service/           # gRPC + REST host (M3); calls AddServiceDefaults()/MapDefaultEndpoints()
  Custodex.Client/            # gRPC client implementing IAuthorizer (M3)
  Custodex.AppHost/           # Aspire app host — orchestrates Postgres + Service for local dev
  Custodex.ServiceDefaults/   # Aspire shared defaults — OTel, health (/health,/alive), discovery, resilience
tests/
  Custodex.Abstractions.Tests/
  Custodex.Core.Tests/
  Custodex.Storage.InMemory.Tests/   # (NEW, m0/04)
  Custodex.Storage.Postgres.Tests/   # also hosts the CTE + index differential harnesses (…Tests.Differential)
  Custodex.Service.Tests/            # (NEW, m3/01) host/gRPC/REST/auth/health tests
  Custodex.Client.Tests/             # (NEW, m3/04)
  Custodex.Conformance/              # shared declarative case suite + runner (M0+)
  Custodex.Benchmarks/               # BenchmarkDotNet (M2+)
```

The DSL (m3/05) lives in `Custodex.Core` under namespace `Custodex.Core.Dsl` with tests in `Custodex.Core.Tests` (no separate `Custodex.Dsl` project). The DI extensions live in existing packages: `AddCustodex()`/`AddCustodexInstrumentation()` in `Custodex.Core`, `.UsePostgres()` in `Custodex.Storage.Postgres` (no separate `Custodex.Extensions.DependencyInjection` project).

See the **Aspire integration** section below before executing M0/01, M1/09, or any M3 plan.

## Canonical public contract

All names below are normative. `Custodex.Abstractions` namespace unless noted.

### References and tuples

```csharp
public readonly record struct EntityRef(string Type, string Id)   // Id "*" = wildcard
{
    public bool IsWildcard => Id == "*";
    public override string ToString() => $"{Type}:{Id}";
}

public readonly record struct SubjectRef(string Type, string Id, string? Relation = null)
{
    public bool IsSubjectSet => Relation is not null;     // e.g. group:vets#member
    public bool IsWildcard => Id == "*";
}

public sealed record ConditionRef(string Name, IReadOnlyDictionary<string, object?> Parameters);

public sealed record RelationTuple(
    EntityRef Object,
    string Relation,
    SubjectRef Subject,
    ConditionRef? Condition = null);
```

`store` and `tenant` are not fields on `RelationTuple`; they are carried by the operation/`TenantContext` so a tuple value is reusable across stores in tests.

```csharp
public readonly record struct TenantContext(string Store, string Tenant);
```

### Request context and conditions

```csharp
public sealed record RequestContext(
    DateTimeOffset Now,
    SubjectRef Subject,
    IReadOnlyDictionary<string, object?> Attributes);   // ad-hoc context values
```

### Decision API

```csharp
public interface IAuthorizer
{
    Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default);
    Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default);
    Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default);
}

public sealed record CheckRequest(
    TenantContext Tenant, EntityRef Object, string Permission, SubjectRef Subject,
    RequestContext Context, bool Explain = false);

public sealed record CheckResult(bool Allowed, ExplainNode? Explain = null);

public sealed record BatchCheckRequest(TenantContext Tenant, IReadOnlyList<CheckItem> Items, RequestContext Context);
public sealed record CheckItem(EntityRef Object, string Permission, SubjectRef Subject);

public sealed record ListObjectsRequest(
    TenantContext Tenant, SubjectRef Subject, string ObjectType, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListObjectsResult(IReadOnlyList<string> ObjectIds, string? ContinuationToken);

public sealed record ListSubjectsRequest(
    TenantContext Tenant, EntityRef Object, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListSubjectsResult(IReadOnlyList<SubjectRef> Subjects, string? ContinuationToken);

public sealed record ExplainNode(string Description, bool Allowed, IReadOnlyList<ExplainNode> Children);
```

### Management API

```csharp
public interface IRelationManager
{
    Task WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default);
    Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(TenantContext tenant, TupleFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default);
}

public sealed record TupleFilter(string? ObjectType = null, string? ObjectId = null, string? Relation = null, string? SubjectType = null, string? SubjectId = null);
public sealed record ChangeLogFilter(DateTimeOffset? Since = null, string? Actor = null, int Limit = 100);
public sealed record ChangeLogEntry(long Id, string Actor, string Operation, string Target, object? Before, object? After, DateTimeOffset OccurredAt);

public interface ISchemaManager
{
    SchemaValidationResult ValidateSchema(Schema schema);
    Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default);
    Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default);
}

public interface IStoreManager { Task CreateStoreAsync(string store, CancellationToken ct = default); }
public interface ITenantManager { Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default); }
```

### Schema model (canonical AST)

```csharp
public sealed record Schema(string Version, IReadOnlyList<EntityTypeDef> Types, IReadOnlyList<ConditionDef> Conditions);

public sealed record EntityTypeDef(string Name, IReadOnlyList<RelationDef> Relations, IReadOnlyList<PermissionDef> Permissions);

// Allowed fillers: ("user", null) any user; ("user","*") wildcard; ("group","member") subject-set.
public sealed record RelationDef(string Name, IReadOnlyList<SubjectTypeRef> AllowedSubjects);
public sealed record SubjectTypeRef(string Type, string? Relation = null, bool Wildcard = false);

public sealed record PermissionDef(string Name, PermExpr Expression);

// PermExpr and ConditionExpr carry no System.Text.Json polymorphism attributes. Serialization to jsonb
// is owned by the storage provider: PermExprJsonConverter (a JsonConverterFactory in
// Custodex.Storage.Postgres) writes a stable "$type" discriminator per concrete node when persisting a Schema.
public abstract record PermExpr;
public sealed record RelationRef(string Relation) : PermExpr;                       // direct relation
public sealed record Union(PermExpr Left, PermExpr Right) : PermExpr;               // a + b
public sealed record Intersect(PermExpr Left, PermExpr Right) : PermExpr;           // a & b
public sealed record Exclude(PermExpr Left, PermExpr Right) : PermExpr;             // a - b
public sealed record Arrow(string Relation, string Permission) : PermExpr;          // relation->permission
public sealed record Conditioned(PermExpr Inner, string ConditionName) : PermExpr;  // branch gated by a condition

public sealed record ConditionDef(string Name, IReadOnlyList<ConditionParam> Parameters, ConditionExpr Body);
public sealed record ConditionParam(string Name, ConditionType Type);
public enum ConditionType { Bool, Int, Long, Double, String, Timestamp }

public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors);
```

### Fluent builder (shape)

```csharp
var schema = new SchemaBuilder("v1")
    .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
    .Type("animal", t => t
        .Relation("medicator", s => s.User().SubjectSet("group", "member"))
        .Relation("enclosure", s => s.Type("enclosure"))
        .Relation("blocked", s => s.User().SubjectSet("group", "member"))
        .Permission("edit", p => p.Relation("medicator").Arrow("enclosure", "edit").Exclude(x => x.Relation("blocked"))))
    .Condition("within_hours", c => c.Int("start").Int("end"))
    .Build();   // returns Schema
```

### Storage provider interfaces (implemented per provider)

```csharp
public interface IRelationStore
{
    Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(TenantContext t, EntityRef obj, string relation, CancellationToken ct = default);
    Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(TenantContext t, SubjectRef subject, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListObjectIdsAsync(TenantContext t, string objectType, CancellationToken ct = default);  // type universe for the ListObjects oracle / wildcard grants
    Task WriteAsync(TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove, IUnitOfWork uow, CancellationToken ct = default);
}
public interface ISchemaStore { Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default); Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default); }
public interface IAttributeStore { Task<IReadOnlyDictionary<string, object?>?> GetAsync(TenantContext t, EntityRef obj, CancellationToken ct = default); Task SetAsync(TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs, IUnitOfWork uow, CancellationToken ct = default); }
public interface IIndexStore { /* M2 */ }
public sealed record CacheEntry(byte[] Value, long Epoch);
// GetAsync returns the stored entry with the epoch it was written at; the caching layer (M0/08)
// compares CacheEntry.Epoch to the current tenant epoch and treats a mismatch as a miss.
public interface ICacheStore { Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default); Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default); Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default); Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default); }
public interface IChangeLogStore { Task AppendAsync(TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default); Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(TenantContext t, ChangeLogFilter filter, CancellationToken ct = default); }

// Transaction seam: the in-memory provider uses a no-op; Postgres enlists in an ambient or supplied DbTransaction.
public interface IUnitOfWork : IAsyncDisposable { Task CommitAsync(CancellationToken ct = default); }
public interface IUnitOfWorkFactory { Task<IUnitOfWork> BeginAsync(CancellationToken ct = default); }
```

### Exceptions

```csharp
public sealed class SchemaValidationException(IReadOnlyList<string> errors) : Exception;  // thrown by SetActiveSchema on invalid schema
public sealed class UnknownTypeException(string type) : Exception;                          // request references a type not in schema
public sealed class UnknownRelationException(string type, string relation) : Exception;
public sealed class UnknownPermissionException(string type, string permission) : Exception;
public sealed class EvaluationLimitException(string detail) : Exception;                    // depth/cycle guard tripped
```

Allow/deny is always a `CheckResult`, never an exception. The exceptions above signal caller or schema errors.

## Post-dispatch contract reconciliations

Decisions made after the milestone plans were drafted in parallel; these are authoritative and supersede any drafted plan text that conflicts. They are applied as each affected plan is executed (see the `rebac-plan-maintenance` memory).

1. **Condition evaluator seam (Custodex.Core).** The injectable seam is a bool-returning interface `IConditionEvaluator { bool Evaluate(ConditionDef definition, ConditionRef invocation, IReadOnlyDictionary<string,object?> resourceAttributes, RequestContext context); }`. The predicate logic lives in the static `ConditionEvaluator` (m0/06), whose `ConditionResult Evaluate(ConditionDef definition, IReadOnlyDictionary<string,object?> attributes, RequestContext context, IReadOnlyDictionary<string,object?> parameters)` returns `ConditionResult(bool Allowed, string? Diagnostic)`. `CelConditionEvaluator` is the adapter that bridges the static evaluator to the interface — mapping `ConditionResult.Allowed` to the bool — and is the `IConditionEvaluator` that `UsePostgres` registers by default; `NullConditionEvaluator` returns `true`. A missing attribute or type mismatch yields `Allowed=false` with a diagnostic, so the adapter returns `false` — never an exception. `EngineDrivenAuthorizer` (m0/05) and `NpgsqlCteAuthorizer` (m1/05) depend on the interface.
2. **Cacheability seam (Custodex.Core).** `EngineDrivenAuthorizer` exposes `internal Task<(bool Allowed, bool ConditionTouched)> CheckInternalAsync(...)` via an internal interface `ICacheableAuthorizer`; `CachingAuthorizer` (m0/08) depends on `ICacheableAuthorizer` and wraps it, caching only `ConditionTouched == false` results. `NpgsqlCteAuthorizer` does not implement `ICacheableAuthorizer`, so the Postgres CTE path that `UsePostgres` registers (m1/09) runs without the cross-request cache; per-request memoization and read-your-writes still hold. A cache wrap over the CTE path would require adding the seam to that authorizer.
3. **`reverse_index` and `tenant_epochs`.** `reverse_index` carries `schema_version` (already in §6.3 DDL, m1/01). `ICacheStore.GetEpochAsync`/`BumpEpochAsync` are backed by a provider-internal `tenant_epochs(store_id, tenant_id, epoch bigint, PK(store_id,tenant_id))` table (m1/07) — distinct from the per-row `cache_entries.epoch` stamp.
4. **`IIndexStore` members (m2/01).** Defined in `Custodex.Abstractions` by m2/01: `UpsertAsync` / `DeleteForObjectAsync` / `QueryObjectsAsync(subject, permission, objectType, paging)` / rebuild markers, all `(store, tenant, schema_version)`-scoped.
5. **Provider references Core.** `Custodex.Storage.Postgres` takes a project reference on `Custodex.Core` (for `SchemaIndex`, `EvalContext`, `ContinuationCursor`, `IConditionEvaluator`). Spec §4 forbids DB code *inside* Core, not Core being referenced by a provider.
6. **Manager registration.** The concrete `IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager` implementations are registered from `Custodex.Storage.Postgres` (they call the provider's in-transaction `AuditedWritePath`); they implement the `Custodex.Abstractions` interfaces, so consumers are unaffected.
7. **Project structure (resolves cross-plan drift).** The milestone plans were drafted in parallel and reference some project names that are not the canonical layout above. When executing a plan, map them as follows — the layout above is authoritative:
   - `Custodex.Service.Tests`, `Custodex.Client.Tests`, `Custodex.Storage.InMemory`, `Custodex.Storage.InMemory.Tests` are **(NEW)** — the introducing plan creates them and adds them to `Custodex.slnx`.
   - `Custodex.Dsl` / `Custodex.Dsl.Tests` → `Custodex.Core` (namespace `Custodex.Core.Dsl`) / `Custodex.Core.Tests`.
   - `Custodex.Extensions.DependencyInjection` / `…​.Tests` → `Custodex.Core` (DI builder) and `Custodex.Storage.Postgres` (`.UsePostgres()`); tested in `Custodex.Core.Tests` / `Custodex.Storage.Postgres.Tests`.
   - `Custodex.Differential` → the differential harnesses live in `Custodex.Storage.Postgres.Tests` (namespace `…​.Differential`).
   - Dotted names like `Custodex.Core.Evaluation`, `Custodex.Service.Rest`, `Custodex.Storage.Postgres.Index` are **namespaces/folders inside** their project, not separate projects.

## Aspire integration

The solution targets .NET Aspire (see the layout above). Every affected plan applies these rules.

- **Projects already exist** — M0/01 and any plan that "creates a project" instead **adds packages/references/source to the existing project**. The solution file is `Custodex.slnx`; add projects to it only if a plan introduces a genuinely new one (none should — the seven src + five test projects already exist).
- **Observability flows through `Custodex.ServiceDefaults`.** `ServiceDefaults` already configures OpenTelemetry (traces/metrics/logs + OTLP export), so plans must NOT build a parallel OTel pipeline. The library still owns `CustodexDiagnostics` (the `"Custodex"` `ActivitySource` + `Meter`, M0/01); the integration point is registering them into the OTel pipeline. Implement `AddCustodexInstrumentation()` (M1/09) as OpenTelemetry builder extensions: `tracing.AddSource("Custodex")` and `metrics.AddMeter("Custodex")`, called from `Custodex.Service` after `AddServiceDefaults()`. Enable Aspire's commented-out `AddGrpcClientInstrumentation()` for the service.
- **Health checks flow through ServiceDefaults.** Reuse `MapDefaultEndpoints()` (`/health`, `/alive`). M3/06's `PostgresHealthCheck` is registered as a health check tagged `"ready"` (consumed by `/health`); do not hand-map `/health/ready` separately.
- **Local orchestration is the AppHost.** `Custodex.AppHost` provisions a Postgres resource and runs `Custodex.Service` against it for development — this replaces docker-compose *for dev*. A production `Dockerfile` (M3/06) still applies for deploying the image.
- **The `Custodex.Service` host** calls `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`; M3/01–03 build their gRPC/REST/auth surface on top of that host rather than a bare `WebApplication`.

## Milestone → plan-file map

### M0 — Engine core (`m0/`)
- `01-solution-and-abstractions.md` — solution, projects, `Custodex.Abstractions` contract above, building/CI. **Owns project-wide packaging** (`Directory.Build.props` with `Apache-2.0` license metadata, semver, symbol packages) and the **`Custodex.Diagnostics` observability primitives** (`ActivitySource` named `"Custodex"` and a `Meter` named `"Custodex"` exposing counter/histogram instruments that later plans populate).
- `02-schema-model-and-builder.md` — the AST records and `SchemaBuilder`.
- `03-schema-validation.md` — resolve relations/permissions, terminating-recursion check, condition type-checking; `SchemaValidationResult`.
- `04-in-memory-providers.md` — in-memory `IRelationStore`/`ISchemaStore`/`IAttributeStore`/`ICacheStore`/`IChangeLogStore` + no-op `IUnitOfWork`.
- `05-check-engine-driven.md` — engine-driven `CheckAsync`: union/intersection/exclusion/arrow/nesting/wildcards, cycle + depth guards, per-request memoization, `Explain`. Emits a `Custodex.Diagnostics` Activity span per check and a latency histogram.
- `06-condition-evaluator.md` — typed predicate evaluator over attributes + context + params; `Conditioned` branches and condition-carrying tuples.
- `07-list-and-batch.md` — engine-driven `ListObjects` (oracle), `ListSubjects`, `BatchCheck`, pagination contract.
- `08-caching-orchestration.md` — per-request memo + cross-request `ICacheStore` cache, unconditioned-only, epoch invalidation.
- `09-conformance-and-property-harness.md` — declarative case format + runner, the six worked examples as cases, CsCheck generators asserting internal invariants.

### M1 — Postgres provider + usable library (`m1/`)
- `01-postgres-schema-and-migrations.md` — DDL for all tables/indexes from spec §6.3; idempotent migrations.
- `02-cte-nested-algebra-spike.md` — the spike (spec §7.1): prove nested exclusion/intersection-through-arrows in recursive CTEs; document the CTE/engine seam.
- `03-unit-of-work-and-enlistment.md` — Npgsql `IUnitOfWork`/factory; ambient + supplied `DbTransaction` enlistment.
- `04-postgres-relation-and-schema-stores.md` — Dapper `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IChangeLogStore`.
- `05-cte-check-path.md` — recursive-CTE `CheckAsync` for the Postgres provider; thin algebra/condition layer over CTE results.
- `06-cte-listobjects-and-pagination.md` — CTE `ListObjects`/`ListSubjects` with over-fetch/refill cursor.
- `07-epoch-cache-and-change-audit.md` — epoch bump in-transaction; `change_log` append on every write.
- `08-differential-harness-cte-vs-oracle.md` — CsCheck harness asserting CTE ≡ engine-driven oracle.
- `09-di-and-usable-library.md` — `AddCustodex().UsePostgres(conn).UseSchema(builder)`; OpenTelemetry registration (`AddCustodexInstrumentation()` wiring the `"Custodex"` ActivitySource + Meter into OTel); end-to-end sample against Testcontainers.

### M2 — Performance (`m2/`)
- `01-reverse-index-schema.md` — `reverse_index` table + `schema_version` stamp + `IIndexStore`.
- `02-reverse-index-full-rebuild.md` — full rebuild from tuples (the safety-net oracle for the index).
- `03-reverse-index-incremental.md` — in-transaction incremental maintenance, including exclusion/arrow closure.
- `04-listobjects-via-index.md` — index-backed `ListObjects` with conditioned-row re-check.
- `05-unlogged-postgres-cache.md` — `ICacheStore` on an `UNLOGGED` table with lazy + swept TTL.
- `06-differential-index-vs-oracle.md` — assert index ≡ oracle, including post-write maintenance.
- `07-benchmarks.md` — BenchmarkDotNet for Check/ListObjects at representative scale.

### M3 — Service / AaaS (`m3/`)
- `01-grpc-contracts.md` — proto for the four ops + management; `Custodex.Service` host.
- `02-rest-and-openapi.md` — minimal-API REST surface + OpenAPI.
- `03-authn-and-multistore.md` — API-key/OIDC auth, store/tenant resolution.
- `04-Custodex-client.md` — gRPC `IAuthorizer` client; DI swap with in-process.
- `05-dsl-parser.md` — text DSL ⇄ canonical `Schema`.
- `06-container-image.md` — production Dockerfile + image smoke test (dev orchestration lives in the AppHost, m3/07; health via ServiceDefaults).
- `07-apphost-and-servicedefaults.md` — wire `Custodex.ServiceDefaults` into `Custodex.Service` (register the `"Custodex"` source/meter, Postgres readiness check), and the `Custodex.AppHost` orchestration (Postgres resource + Service reference) for local dev.

## Conventions for every plan

- Use the writing-plans format: plan header, `### Task N`, bite-sized `- [ ]` steps, **complete code in every step**, exact paths, exact commands with expected output, a commit step per task.
- TDD: failing test → run (see it fail) → minimal implementation → run (green) → commit.
- Reference types by the exact names in this contract. If a plan needs a new shared type, add it here first.
- xUnit + Shouldly; one behavior per test; Testcontainers for any Postgres test.

### Calibration — where the rigor goes

The engine does not exist yet, so for the algorithm-heavy plans the *tests* are the durable, correct specification; the *implementation code* is a candidate to be proven by tests, the M1/02 CTE spike, and the differential harnesses (M1/08, M2/06).

- **Tests must be correct now.** Write them to pin real, checkable behaviour (the six worked examples, the algebra laws, the index ≡ oracle invariant). They are the contract.
- **For `m1/05`, `m1/06`, `m2/03` (recursive-CTE evaluation and incremental reverse-index closure):** present the implementation as the approach to validate, explicitly noted as "validated by the spike/differential harness," not as guaranteed-correct copy-paste. Do not pretend the plan pre-solves the recursion; the spike and harness are the correctness mechanism.

## Maintenance

These plans are living documents. As implementation reveals better choices, update the affected plan **and** this contract, keep the two consistent, and note material design changes. The project memory `rebac-plan-maintenance` describes the workflow.
