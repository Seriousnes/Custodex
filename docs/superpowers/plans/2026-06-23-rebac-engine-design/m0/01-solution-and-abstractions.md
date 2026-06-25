# M0/01 — Solution & Abstractions

**Goal:** Stand up project-wide packaging (`Directory.Build.props` with Apache-2.0 metadata, semver, symbol packages), the observability primitives (`CustodexDiagnostics`), and the complete `Custodex.Abstractions` public contract — the records, interfaces, enums, and exceptions every other package depends on.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** `Custodex.Abstractions` is contracts only — value records, interfaces, enums, exceptions, with no logic beyond trivial computed members on value types (`EntityRef.IsWildcard`, `SubjectRef.IsSubjectSet`, `ToString` overrides). The Aspire solution (`Custodex.slnx`) and the `Custodex.Abstractions` + `Custodex.Abstractions.Tests` shells already exist; this plan adds packages, references, and source — it does not scaffold projects. Every type name and signature is normative and defined once in `../README.md` → Canonical public contract; reference it there.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly.

**Global Constraints:** see `../README.md` → Global Constraints (packaging metadata, `TreatWarningsAsErrors`, async `CancellationToken` trailing param, ordinal identifiers, `"*"` wildcard).

**Dependencies:** none — this is the root of the dependency graph.

---

### Task 1: Packaging and test wiring

- [ ] **Files:** create/merge `Directory.Build.props` (repo root); wire `tests/Custodex.Abstractions.Tests` → `src/Custodex.Abstractions` reference + Shouldly; remove any template leftovers (`Class1.cs`, `UnitTest1.cs`). Test: `tests/Custodex.Abstractions.Tests/WiringTests.cs`.

**Produces:** a buildable `Custodex.Abstractions` assembly and a wired test project; the project-wide MSBuild properties.
**Consumes (see README):** nothing — root.

**Behavior:**
- `Directory.Build.props` sets the framework/language/nullable/implicit-usings/`TreatWarningsAsErrors` and the package metadata (`Apache-2.0` license expression, authors, semver `Version`, symbol package format) per the Global Constraints. Merge into any existing file; the Aspire host/service-defaults projects keep their own SDK and may override `TargetFramework`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| reference `Custodex.Abstractions` from the test project | the assembly is loadable and named `Custodex.Abstractions` |

**Done when:** build clean under TreatWarningsAsErrors; wiring test passes.

---

### Task 2: Reference and tuple types

- [ ] **Files:** create `EntityRef.cs`, `SubjectRef.cs`, `RelationTuple.cs` (holds `ConditionRef` + `RelationTuple`), `TenantContext.cs`. Test: `tests/Custodex.Abstractions.Tests/ReferenceTypesTests.cs`.

**Produces:** `EntityRef`, `SubjectRef`, `ConditionRef`, `RelationTuple`, `TenantContext`.
**Consumes (see README):** these types' exact shapes are in the contract.

**Behavior:**
- `EntityRef` and `SubjectRef` are readonly record structs with computed `IsWildcard` (`Id == "*"`) and, for `SubjectRef`, `IsSubjectSet` (`Relation is not null`) plus a `ToString` that renders `type:id` or `type:id#relation`.
- `store`/`tenant` are carried by `TenantContext`, not fields on `RelationTuple`, so a tuple value is reusable across stores in tests.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `EntityRef(type, "*")` vs a concrete id | `IsWildcard` true / false; `ToString` → `type:id` |
| `SubjectRef` with a relation vs without | `IsSubjectSet` true / false; wildcard id detected |

**Done when:** build clean; computed members behave.

---

### Task 3: Request, result, and decision API

- [ ] **Files:** create `RequestContext.cs`, `Requests.cs`, `IAuthorizer.cs`. Test: `tests/Custodex.Abstractions.Tests/RequestTypesTests.cs`.

**Produces:** `RequestContext`; `CheckRequest`/`CheckResult`; `BatchCheckRequest`/`CheckItem`; `ListObjectsRequest`/`ListObjectsResult`; `ListSubjectsRequest`/`ListSubjectsResult`; `ExplainNode`; `IAuthorizer`.
**Consumes (see README):** all signatures are in the contract.

**Behavior:**
- The four operations (`CheckAsync`, `BatchCheckAsync`, `ListObjectsAsync`, `ListSubjectsAsync`) on `IAuthorizer` are the engine's whole decision surface. List requests default `PageSize = 100` and a null continuation token.

**Cases to pin:**

| Setup | Expect |
|---|---|
| construct a `ListObjectsRequest` with defaults | `PageSize == 100`; `ContinuationToken` null |

**Done when:** build clean; defaults hold.

---

### Task 4: Management interfaces

- [ ] **Files:** create `Management.cs`. Test: `tests/Custodex.Abstractions.Tests/ManagementContractTests.cs`.

**Produces:** `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager`; `TupleFilter`, `ChangeLogFilter`, `ChangeLogEntry`.
**Consumes (see README):** `Schema`, `SchemaValidationResult` (Task 5) — Task 5 must be present for the assembly to compile.

**Behavior:**
- The management seams are how a consuming app writes tuples/attributes, reads them back filtered, manages the active schema, and reads the change log. Concrete implementations land per provider.

**Cases to pin:**

| Setup | Expect |
|---|---|
| construct `ChangeLogFilter` with defaults | `Limit == 100` |

**Done when:** build clean; defaults hold.

---

### Task 5: Schema AST and validation result

- [ ] **Files:** create `Schema.cs` and `ConditionExpr.cs`. Test: `tests/Custodex.Abstractions.Tests/SchemaAstTests.cs`.

**Produces:** `Schema`, `EntityTypeDef`, `RelationDef`, `SubjectTypeRef`, `PermissionDef`; the `PermExpr` hierarchy (`RelationRef`, `Union`, `Intersect`, `Exclude`, `Arrow`, `Conditioned`); `ConditionDef`, `ConditionParam`, `ConditionType`; `SchemaValidationResult`; and the abstract `ConditionExpr` marker (its concrete body nodes are owned by m0/06).
**Consumes (see README):** the AST shapes are in the contract.

**Behavior:**
- `PermExpr` is a sealed, closed set the engine switches over exhaustively; the abstract base plus six concrete nodes is the whole permission algebra. The AST is designed to serialize to jsonb and the DSL; the polymorphic JSON wiring is added when serialization lands, not in this task.
- `ConditionExpr` ships here as an abstract marker only; m0/06 adds the concrete body nodes.

**Cases to pin:**

| Setup | Expect |
|---|---|
| a `Union(RelationRef, Arrow)` value | pattern-matches as `Union`; subtypes are matchable |

**Done when:** build clean; subtypes pattern-match.

---

### Task 6: Storage provider interfaces, unit of work, cache, exceptions

- [ ] **Files:** create `Storage.cs` and `Exceptions.cs`. Test: `tests/Custodex.Abstractions.Tests/ExceptionsTests.cs`.

**Produces:** `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IIndexStore` (empty until M2), `ICacheStore` + `CacheEntry`, `IChangeLogStore`, `IUnitOfWork`, `IUnitOfWorkFactory`; the exception hierarchy (`SchemaValidationException`, `UnknownTypeException`, `UnknownRelationException`, `UnknownPermissionException`, `EvaluationLimitException`).
**Consumes (see README):** all storage and exception signatures are in the contract.

**Behavior:**
- `IRelationStore` exposes `GetByObjectAsync`, `GetBySubjectAsync`, `ListObjectIdsAsync` (the type universe for the ListObjects oracle and wildcard grants), and a batched `WriteAsync(add, remove, uow)`. Every storage operation takes a `TenantContext` and filters on `(store, tenant)`.
- `ICacheStore.GetAsync` returns a `CacheEntry` carrying the epoch it was written at; the caching layer (m0/08) compares it to the current tenant epoch and treats a mismatch as a miss.
- Allow/deny is always a `CheckResult`; the exceptions signal caller or schema errors (unknown type/relation/permission, invalid schema, tripped depth/cycle guard) and carry the offending name.

**Cases to pin:**

| Setup | Expect |
|---|---|
| construct `UnknownTypeException(type)` | the message contains the type name |

**Done when:** build clean; exception carries its detail.

---

### Task 7: Diagnostics primitives

- [ ] **Files:** create `CustodexDiagnostics.cs`. Test: `tests/Custodex.Abstractions.Tests/DiagnosticsTests.cs`.

**Produces:** `CustodexDiagnostics` — the `ActivitySource` and `Meter` both named `"Custodex"`, plus the instruments later plans record into: `CheckDuration` (`Custodex.check.duration`, unit `ms`), `CacheHits` (`Custodex.cache.hits`), `CacheMisses` (`Custodex.cache.misses`).
**Consumes (see README):** observability flows into the OTel pipeline via `Custodex.ServiceDefaults` and `AddCustodexInstrumentation()` (M1); this task owns only the source/meter/instruments.

**Behavior:**
- The library owns the `"Custodex"` `ActivitySource` and `Meter`; M0/05 and M0/08 populate the instruments. The integration point (registering them into OTel) is M1, not here.

**Cases to pin:**

| Setup | Expect |
|---|---|
| read the source/meter names | both are `"Custodex"` |

**Done when:** build clean; names match.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] Every type in `../README.md` → Canonical public contract exists with its exact signature, except the concrete `ConditionExpr` nodes (m0/06) and `IIndexStore` members (M2).
- [ ] `IRelationStore` exposes `ListObjectIdsAsync` for the ListObjects oracle.
- [ ] No logic beyond computed members on value types lives in `Custodex.Abstractions`.
- [ ] Every public type and member carries an XML doc comment (this is the consumer-facing surface).
