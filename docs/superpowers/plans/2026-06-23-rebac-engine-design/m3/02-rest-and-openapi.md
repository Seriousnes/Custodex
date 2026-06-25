# M3/02 — REST Surface & OpenAPI

**Goal:** Add a minimal-API REST surface to `Custodex.Service` over the **same** engine the gRPC services use (`m3/01`): decision endpoints (`/v1/check`, `/v1/batch-check`, `/v1/list-objects`, `/v1/list-subjects`) and management endpoints for tuples, attributes, change-log, schema, stores, and tenants, documented with OpenAPI/Swagger (spec §10.1, §11.4).

**For implementers:** drive this plan with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each task is TDD — Red → Green → Commit — tracked by its `- [ ]` checkbox. One Conventional Commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture:** REST is a second thin front door beside gRPC, depending only on the public `Custodex.Abstractions` interfaces resolved from the same DI scope. Plain `record` DTOs model the JSON bodies; a `RestMap` static class converts DTO ⇄ contract record once, mirroring `m3/01`'s `ProtoMap`. Heterogeneous attribute/parameter values ride as `Dictionary<string, object?>` so arbitrary JSON values round-trip; schema travels as the canonical JSON `SchemaJson` (`m3/01`) produces. Endpoints live under a `/v1` group split into `decision`- and `management`-tagged subgroups so `m3/03` can attach distinct authorization policies per group.

**Tech stack:** .NET 10 / C# 14, ASP.NET Core minimal APIs, `Microsoft.AspNetCore.OpenApi` + `Swashbuckle.AspNetCore`, the engine packages, xUnit + Shouldly, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints. No EF Core; adds zero evaluation semantics.

**Dependencies (see README):** the `Custodex.Abstractions` contract; `AddCustodex().UsePostgres()` and the managers; the `Custodex.Service` host and `SchemaJson` from `m3/01`. Per `../README.md` → Aspire integration, reuse ServiceDefaults' `/health` and `/alive` — do not add bespoke health endpoints.

## Shared decisions

- **Same engine, same DI.** REST endpoints resolve the identical `IAuthorizer` + manager interfaces the gRPC services use; both front doors share one engine instance per request scope.
- **`/v1` route group, `decision` vs `management` tags.** `m3/03` attaches a read policy to the decision group and a write policy to the management group via `.RequireAuthorization(...)` on the group builders.
- **DTOs are plain records; `RestMap` is the single converter.** `Dictionary<string, object?>` carries attributes and condition parameters.
- **Tenancy explicit in the bodies here.** Request DTOs carry `Store` + `Tenant` (or `Store` for schema endpoints); `m3/03` moves resolution to headers/claims.
- **Deny is `200 OK` with `allowed:false`; errors are HTTP errors** (spec §10.3). A malformed or invalid schema is `400`; an unknown type/relation/permission surfaces as `400` via the typed exceptions; default-deny never throws.

---

### Task 1: OpenAPI/Swagger and the `/v1` route groups

- [ ] **Files:** add the OpenAPI packages to `src/Custodex.Service/Custodex.Service.csproj`; register Swagger and call `MapCustodexRest()` in `Program.cs`; add `Rest/RestEndpoints.cs`; test `…Tests/OpenApiTests.cs`.

**Produces:** `RestEndpoints.MapCustodexRest(this WebApplication)` mapping a `/v1` group with `decision`- and `management`-tagged subgroups (endpoints filled by later tasks); Swagger UI at `/swagger` and the document at `/swagger/v1/swagger.json`.
**Consumes (see README):** the `Custodex.Service` host (`m3/01`); `Swashbuckle.AspNetCore`.

**Behavior:** `RestEndpoints` is a `partial` static class. The decision and management endpoint maps are declared as partial methods with no body now (filled by Tasks 3 and 5), so the route-group skeleton compiles and serves Swagger before the endpoints exist — an unimplemented partial method compiles to a no-op. The OpenAPI document names the API (title "Custodex Authorization API").

**Cases to pin:**

| Setup | Expect |
|---|---|
| GET `/swagger/v1/swagger.json` | 200, body contains the API title |

**Done when:** build clean; the OpenAPI document is served and names the API.

---

### Task 2: Decision DTOs and `RestMap`

- [ ] **Files:** add `Rest/DecisionDtos.cs`, `Rest/RestMap.cs`; test `…Tests/RestMapTests.cs`.

**Produces:** decision DTOs (`EntityRefDto`, `SubjectRefDto`, `RequestContextDto`, `CheckRequestDto`/`CheckResponseDto`, `ExplainNodeDto`, `BatchCheckRequestDto`/`CheckItemDto`/`BatchCheckResponseDto`, `ListObjectsRequestDto`/`ListObjectsResponseDto`, `ListSubjectsRequestDto`/`ListSubjectsResponseDto`) and `RestMap` with the DTO ⇄ contract-record converters, including a `Tenant(store, tenant)` helper.
**Consumes (see README):** `EntityRef`, `SubjectRef`, `RequestContext`, `ExplainNode`, and the request/result records.

**Behavior:** the same null-`Relation` rule as gRPC — `SubjectRefDto.Relation` is `string?`; null/absent ⇒ plain subject, non-null ⇒ subject-set. `RequestContextDto.Attributes` is `Dictionary<string, object?>?` (null ⇒ empty); a null `Now` defaults. `RestMap` is the single home for the mapping so it is tested in isolation like `ProtoMap`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `EntityRefDto("user","*")` round-trip | `IsWildcard` true, equal back |
| `SubjectRefDto("user","alice")` / with relation | `IsSubjectSet` false / true |
| `RequestContextDto` with null `Now` + attributes | subject + attributes mapped, `Now` defaulted |
| `ExplainNode` with a child | maps recursively |
| `RestMap.Tenant("zoo","t1")` | `TenantContext("zoo","t1")` |

**Done when:** build clean; cases pass; `RestMap` round-trips every shared type including the null-`Relation` rule.

---

### Task 3: Decision REST endpoints

- [ ] **Files:** add `Rest/DecisionEndpoints.cs` (the `MapDecisionEndpoints` partial body); test `…Tests/DecisionRestTests.cs`.

**Produces:** `POST /v1/check`, `/v1/batch-check`, `/v1/list-objects`, `/v1/list-subjects`, each delegating to `IAuthorizer` via `RestMap`, named + summarized for OpenAPI.
**Consumes (see README):** `IAuthorizer` and its request/result records; `RestMap` + the decision DTOs (Task 2).

**Behavior:** a denied check returns `200` with `{"allowed":false}` (spec §10.3); `Explain` is included only when requested. Endpoints inject `IAuthorizer` per call. A non-positive `PageSize` defaults; `ContinuationToken` passes through.

| method + route | handler purpose |
|---|---|
| `POST /v1/check` | point check → `CheckResponseDto{allowed, explain?}` |
| `POST /v1/batch-check` | many checks sharing one context → aligned results |
| `POST /v1/list-objects` | objects of a type a subject may act on (+ cursor) |
| `POST /v1/list-subjects` | subjects who may act on an object (+ cursor) |

**Cases to pin:**

| Setup | Expect |
|---|---|
| check, granted subject | 200, `allowed` true |
| check, ungranted subject | 200, `allowed` false |
| check with `Explain=true` | `allowed` true, `Explain` present |
| list-objects for a subject with two grants | both ids |
| batch-check {granted, ungranted} | two aligned results, true then false |

**Done when:** build clean; cases pass; deny is `200` with `allowed:false`, explain returned on request.

---

### Task 4: Management DTOs + tuple converters

- [ ] **Files:** add `Rest/ManagementDtos.cs`; extend `RestMap` with `ToTuple`/`FromTuple`; test `…Tests/ManagementDtoTests.cs`.

**Produces:** management DTOs — `ConditionRefDto`, `RelationTupleDto`, `WriteTuplesRequestDto`, `WriteAttributesRequestDto`, `ReadTuplesRequestDto`/`ReadTuplesResponseDto`, `ChangeLogEntryDto`/`ReadChangeLogRequestDto`/`ReadChangeLogResponseDto`, `ValidateSchemaRequestDto`/`ValidateSchemaResponseDto`, `SetActiveSchemaRequestDto`, `GetActiveSchemaResponseDto`, `CreateStoreRequestDto`, `CreateTenantRequestDto` — plus the `RestMap` tuple converters.
**Consumes (see README):** `RelationTuple`, `ConditionRef`, `TupleFilter`, `ChangeLogEntry`, `ChangeLogFilter`.

**Behavior:** `RelationTupleDto` mirrors `RelationTuple(Object, Relation, Subject, Condition?)`; a null `Condition` DTO maps to an unconditioned tuple. `ConditionRefDto(Name, Parameters)` carries `Dictionary<string, object?>` parameters. Schema bodies travel as the `SchemaJson` canonical string.

**Cases to pin:**

| Setup | Expect |
|---|---|
| tuple DTO with a condition + subject-set | object/subject/condition map; parameters round-trip; `IsSubjectSet` true |
| tuple DTO without a condition | `Condition` null |

**Done when:** build clean; cases pass; tuple ⇄ DTO round-trips, including the condition.

---

### Task 5: Management REST endpoints

- [ ] **Files:** add `Rest/ManagementEndpoints.cs` (the `MapManagementEndpoints` partial body); test `…Tests/ManagementRestTests.cs`.

**Produces:** the management endpoints, each delegating to a manager interface.
**Consumes (see README):** the four manager interfaces; `RestMap` + management DTOs (Task 4); `SchemaJson` (`m3/01`).

**Behavior:** `PUT /v1/schema/{store}` catches `SchemaValidationException` and returns `Results.ValidationProblem` with the errors (spec §10.3 — a malformed schema is an error, not a deny). `DELETE /v1/tuples` takes the same body as the write to identify the tuples to remove.

| method + route | handler purpose |
|---|---|
| `POST /v1/tuples` | write relation tuples (audited) |
| `DELETE /v1/tuples` | delete relation tuples (audited) |
| `PUT /v1/attributes` | sync authz-relevant resource attributes |
| `POST /v1/tuples/query` | admin/audit: read tuples by filter |
| `POST /v1/change-log/query` | config-change audit history |
| `POST /v1/schema/validate` | validate a schema without activating it |
| `PUT /v1/schema/{store}` | validate + activate the store's schema (invalid ⇒ 400) |
| `GET /v1/schema/{store}` | fetch the store's active schema |
| `POST /v1/stores` | provision a store (201) |
| `POST /v1/tenants` | provision a tenant in a store (201) |

**Cases to pin:**

| Setup | Expect |
|---|---|
| provision store + tenants → set schema → write tuple → query tuples → query change-log | each round-trips; the written tuple and a `write` log entry appear |
| set schema where a permission references a missing relation | 400 |
| write then delete a tuple → query tuples | empty |

**Done when:** build clean; cases pass; provisioning, schema activation, and audited tuple write/read/delete round-trip; invalid schema returns `400`.

---

### Task 6: Surface the typed engine exceptions as HTTP problems

- [ ] **Files:** add `Rest/ExceptionHandling.cs` (`UseCustodexProblemDetails`); register it in `Program.cs` before `MapCustodexRest()`; test `…Tests/RestErrorHandlingTests.cs`.

**Produces:** `RestEndpoints.UseCustodexProblemDetails(this WebApplication)` — an exception handler mapping the engine's typed exceptions to `ProblemDetails`.
**Consumes (see README):** the typed engine exceptions; `Microsoft.AspNetCore.Diagnostics`.

**Behavior:** caller-bug exceptions are surfaced loudly rather than silently denied (spec §10.3) — a `Check` against an undefined permission throws `UnknownPermissionException` and returns `400`, not a deny. Allow/deny remains a `200` body.

| exception | status |
|---|---|
| `UnknownTypeException` / `UnknownRelationException` / `UnknownPermissionException` | 400 |
| `SchemaValidationException` | 400 |
| `EvaluationLimitException` | 422 |
| (anything else) | 500 |

**Cases to pin:**

| Setup | Expect |
|---|---|
| check against an undefined permission | 400 (not a silent deny) |

**Done when:** build clean; the case passes; the full `Custodex.Service.Tests` suite (gRPC + REST) is green over Testcontainers Postgres.

---

## Self-review checklist

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [ ] REST endpoints resolve the same `IAuthorizer` + manager interfaces the gRPC services use; no evaluation/persistence logic in the endpoints.
- [ ] All four decision endpoints answer correctly; deny is `200` with `allowed:false`; explain returns on request.
- [ ] Management endpoints round-trip through the manager interfaces; an invalid schema returns `400`.
- [ ] `RestMap` round-trips every shared type, including the null-`Relation` rule and the tuple/condition mapping.
- [ ] OpenAPI is served at `/swagger/v1/swagger.json` (UI at `/swagger`); typed exceptions surface as `ProblemDetails`; the full suite is green.
