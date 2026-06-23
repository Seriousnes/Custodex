# ReBAC Engine — Design

- **Date:** 2026-06-23
- **Status:** Approved design, pending implementation plan
- **Working name:** `Relkit` (placeholder; rename freely before implementation)

## 1. Overview

A runtime-configurable **relationship-based access control (ReBAC) engine** with an **attribute-based (ABAC) condition layer**, shipped as a reusable .NET library and, later, as a standalone authorization service. It is modelled on Google Zanzibar (the lineage behind Permify, SpiceDB, and OpenFGA) and is permissively licensed so it can be embedded in commercial applications and shared as open source.

The first consumer is a multi-tenant animal-management SaaS for zoos and wildlife parks (a Blazor Server application). The engine itself contains no domain concepts from that application; the zoo model is supplied as configuration, exactly as any other consumer would supply its own.

### 1.1 Motivating requirements

Tenants configure permissions entirely through the consuming application's UI, with no developer involvement. The engine supports, as worked examples (Section 12):

- Role-based grants over resource categories ("vets may record drug dispensing").
- Team/group grants over a hand-curated set of resources ("the macropods round may edit a specific list of species").
- Site-scoped access where a grant flows through a location and does not leak to other locations.
- Direct grants to an individual user on a single resource instance.
- Conditional access that depends on a resource's structural position (quarantine clearance) and on request-time attributes (time windows, numeric thresholds, ownership).

The engine answers four questions: point **Check**, **ListObjects** (which resources a subject may act on, for filtering list views), **ListSubjects** (who may act on a resource), and **BatchCheck**.

## 2. Glossary

| Term | Meaning |
|---|---|
| **Engine** | The reusable library. Generic primitives only; no domain concepts. |
| **Store** | One consuming application. Owns a single versioned schema lineage. |
| **Tenant** | A data-isolation partition within a Store (one zoo). |
| **Application schema** | The entity types, relations, permissions, and condition definitions for a Store. Authored once by the consuming application's developer; shared by all the Store's tenants. |
| **Entity type** | A named kind of object (`user`, `group`, `site`, `animal`). |
| **Relation** | A named directed edge declared on a type: a grant slot or a structural reference. |
| **Permission** | A named, computed check on a type, derived from relations via the algebra. The thing `Check` evaluates. |
| **Tuple** | An atomic stored fact: `object#relation@subject`. |
| **Subject-set** | A relation reference used as a subject, e.g. `group:vets#member`, enabling nesting. |
| **Condition** | A named, typed predicate (ABAC) attached to a tuple or permission branch, evaluated at request time. |
| **PDP / PEP** | Policy Decision Point (this engine) / Policy Enforcement Point (the consuming application). The engine decides; the application enforces at its own operation boundaries. |

## 3. The three layers

1. **Engine** — generic primitives (types, relations, permissions, tuples, conditions, evaluation). Reusable across any application. Knows nothing about zoos.
2. **Application schema** — authored once by the consuming application's developer (entity types and how permissions are computed). Shared across all that Store's tenants; versioned with the application.
3. **Tenant data** — groups, memberships, grants, exclusions, conditioned grants, and synced resource attributes. Authored by tenant administrators through the application's UI, with no developer involvement. This is the runtime-configurable layer, expressed entirely as data.

"Runtime configurable with no engineers in the loop" applies to layer 3. Layer 2 is a deliberate, versioned developer artifact.

## 4. Architecture and packages

```
┌─────────────────────────────────────────────────────────────┐
│  Consuming app (Blazor Server zoo SaaS, or any .NET app)     │
│  - defines its Application Schema once (fluent C# or DSL)     │
│  - calls Check / ListObjects / ListSubjects / BatchCheck     │
│  - writes tuples + attributes when domain data changes       │
└───────────────┬─────────────────────────────┬───────────────┘
                │ in-process (NuGet ref)       │ over network (gRPC/REST)
                ▼                              ▼
┌──────────────────────────────┐   ┌──────────────────────────────┐
│  Relkit.Core (engine)        │◄──│  Relkit.Service (host)       │
│  • Schema model & validation │   │  thin gRPC + REST wrapper    │
│  • Tuple model               │   │  over the SAME Relkit.Core   │
│  • Evaluation (4 ops)        │   │  + authn, tenancy, OpenAPI   │
│  • Condition (ABAC) eval     │   └──────────────────────────────┘
│  • Caching + invalidation    │
└───────────────┬──────────────┘
                │ IRelationStore / ISchemaStore / IAttributeStore
                │ IIndexStore / ICacheStore
                ▼
┌──────────────────────────────┐
│  Relkit.Storage.Postgres     │   first provider; the interface
│  tuples, schema, attrs,      │   seam keeps the engine DB-agnostic
│  reverse index, cache        │
└──────────────────────────────┘
```

| Package | Contents |
|---|---|
| `Relkit.Abstractions` | Public contracts: `IAuthorizer`, `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager`, and the storage/cache provider interfaces. No implementation. This is the dependency for consumers and third-party providers. |
| `Relkit.Core` | The engine: schema model, evaluation, condition evaluation, caching orchestration. Depends only on `Relkit.Abstractions`. Contains zero domain concepts and zero database code. |
| `Relkit.Storage.Postgres` | Implements the storage and cache provider interfaces against Postgres. The first and only provider built initially. |
| `Relkit.Service` | ASP.NET host exposing gRPC + REST over `Relkit.Core`. Built in milestone M3. The library does not depend on it. |
| `Relkit.Client` | A .NET client for the service that implements the same `IAuthorizer` interface over gRPC, so a consumer switches between in-process and remote by changing one DI registration. Built in M3. |

The zoo application references `Relkit.Core` and `Relkit.Storage.Postgres` directly and gets in-process checks. A separate AaaS deployment runs `Relkit.Service` as a container. Both share one engine, one set of semantics, and one test suite.

## 5. Schema model

The application schema is built from four concepts.

### 5.1 Entity types

| Type | Role | Example tenant instances |
|---|---|---|
| `user` | subjects | dr-smith, alice |
| `group` | groups users; nestable; carries a tenant-defined `kind` (`role`, `team`, `round`, …) the engine never interprets | vets (kind=role), macropods (kind=team), birds-round (kind=round) |
| `site` | scoping node: resources belong to it and users are assigned to it | sydney-park, melbourne-park |
| resource / collection types | the application's objects and their groupings | species, enclosure, animal, inventory_item, category |

`group` carries a `kind` column purely for the consuming application's categorization. A "team" and a "role" are both `group` rows with different `kind` values, which keeps the model open for any future grouping and for arbitrary AaaS consumers.

### 5.2 Relations

A relation is a named directed edge declared on a type, listing the subject types or subject-sets that may fill it. Relations are **grant slots** (named after the application's actions, never after tenant concepts) and **structural references**.

```
type group:
  relation member: user | group#member          # nesting via subject-set

type animal:
  relation dispenser, medicator                  # action-flavoured grant slots
  relation enclosure: enclosure                  # structural reference
  relation species:   species
  relation site:      site
  relation blocked:   user | group#member        # exclusion slot
```

### 5.3 Permissions and the algebra

A permission is a named computed check, defined by an expression over relations using four operators:

- **union** `a + b` — viewer or editor.
- **intersection** `a & b` — vet *and* quarantine-trained.
- **exclusion** `a - b` — everyone *except* revoked (this is how revokes are expressed).
- **arrow / traversal** `rel->perm` — inherit from a related object: `enclosure->edit` resolves to whoever may edit this animal's enclosure.

```
type animal:
  permission edit = medicator + enclosure->edit + species->edit + site->edit - blocked
```

Permissions may nest (`manage ⊃ edit ⊃ view`) or be fully independent (`dispense` grantable on its own). Action and relation names are opaque strings to the engine; adding `dispense`, `feed`, `medicate`, or `record_observation` is a schema edit in the consuming application and requires no engine change.

### 5.4 Conditions (ABAC)

A condition is a named, typed predicate function declared in the schema. It evaluates against three inputs: **synced resource attributes**, **request context** (`now`, the requesting subject, ad-hoc values), and the tuple's stored **parameters**.

```
condition within_hours(start: int, end: int)  = context.now.hour >= start && context.now.hour < end
condition at_least(field: string, n: int)     = resource[field] >= n
condition is_creator()                        = resource.created_by == context.subject
```

A condition's *function* is schema (developer-defined); its *parameters* are tenant data, stored on the tuple that carries the condition.

### 5.5 Authoring surfaces

One canonical in-memory schema model, with two equivalent authoring forms:

- **Fluent C#** at application startup — the primary surface for the in-process library.
- **DSL / JSON over the API** — the same model, for the service path and tooling, built in M3.

Both forms parse and serialize to the canonical model, which the engine validates: every relation and permission resolves, recursion terminates, and conditions type-check. The schema is per-Store and versioned with the application; additive changes are safe and are the normal evolution path.

### 5.6 Modelling guidance: relationships for structure, conditions for predicates

Structural scoping is expressed as relationships so it stays indexable and listable. Request-time predicates are expressed as conditions. A rule such as "only quarantine-trained vets may access animals in a quarantine enclosure" is structural and is modelled with relationships and intersection (Section 12.5), not as an attribute comparison. Conditions are reserved for values that genuinely vary per request: time windows, numeric thresholds, and ownership.

## 6. Data and storage model

### 6.1 Hierarchy

```
Store   (one per consuming app — owns one versioned schema lineage)
  └─ Tenant   (data-isolation partition; the zoo app has many)
       └─ Relation tuples + object attributes
```

The zoo application is one Store with many Tenants. A small single-tenant consumer is one Store with one Tenant. An AaaS customer is its own Store.

### 6.2 The tuple

```
(store, tenant)  object_type:object_id # relation @ subject_type:subject_id[#subject_relation]  [with condition(params)]
```

Example: `category:drugs#dispenser@group:vets#member with within_hours(start=8, end=18)`.

### 6.3 Tables (Postgres provider)

| Table | Columns (essential) |
|---|---|
| `stores` | id, name |
| `schema_versions` | store_id, version, definition (jsonb), is_active |
| `tenants` | store_id, tenant_id |
| `relation_tuples` | store_id, tenant_id, object_type, object_id, relation, subject_type, subject_id, subject_relation (null), condition_name (null), condition_params (jsonb, null) |
| `object_attributes` | store_id, tenant_id, object_type, object_id, attributes (jsonb) |
| `reverse_index` | store_id, tenant_id, subject, permission, object_type, object_id, conditioned (bool) — the maintained expansion (M2) |
| `cache_entries` | UNLOGGED; key, value, epoch, expires_at — the Postgres-native cache (M2) |

`relation_tuples` is indexed in both directions: forward on `(store, tenant, object_type, object_id, relation)` for Check, and reverse on `(store, tenant, subject_type, subject_id)` for traversal and ListSubjects. Every query is filtered by `store_id + tenant_id`; composite indexes provide isolation at the target scale, with row-level security or partitioning available for an AaaS deployment that requires hard isolation.

### 6.4 Attribute synchronization

The consuming application pushes the authz-relevant resource fields into `object_attributes` when domain data changes, the same way it writes tuples. The engine is self-contained: conditions and the candidate-filtering step of ListObjects read attributes directly, in one store, working identically in-process and over the network. Ambient values (`now`, the requesting subject) are supplied as request context, not stored.

## 7. Evaluation engine

The algebra — union, intersection, exclusion, arrow traversal, group nesting, and conditions — is implemented in `Relkit.Core`. Two execution paths produce identical results.

### 7.1 Execution paths

- **Postgres provider — recursive CTEs (primary path).** This is what the zoo application runs. CTEs handle the recursive parts natively: nested-group expansion, arrow inheritance, ListObjects candidate generation, and reverse-index maintenance. Recursive reachability runs in the CTE; intersection, exclusion, and condition evaluation are applied in a thin layer over the CTE result set rather than contorted into a single query.
- **Engine-driven traversal (supported alternative).** A C# walk over the algebra that issues simple, batched, indexed lookups through `IRelationStore`. It serves three purposes: the portable path for any non-Postgres provider, an in-memory provider for fast unit tests, and the differential oracle that proves the CTE path correct. A provider may additionally override a hot path with a native query as an optimization; the portable path never depends on that.

Keeping the storage contract small (`getTuples(object, relation)`, `getTuples(subject)`) is what delivers database-agnosticism.

### 7.2 The four operations

| Operation | Strategy |
|---|---|
| **Check** | Walk the permission expression from the object; resolve relations via batched lookups; recurse arrows and nested groups; short-circuit union and intersection; apply conditions as conditioned tuples are reached. Cycle-guarded and depth-bounded. |
| **ListSubjects** | Forward-expand the permission tree from the object down to leaf `user`s. |
| **ListObjects** | See Section 7.3. |
| **BatchCheck** | Many `(object, subject, permission)` requests sharing one per-request memoization cache; a same-type page folds into a ListObjects-style pass. |

### 7.3 ListObjects in two milestones

- **Milestone 1 — correctness oracle.** Reverse-traverse from the subject: gather tuples where the subject (or a group it belongs to) appears, walk forward to candidate objects of the target type, confirm the permission holds, then evaluate conditions per candidate. Always correct; heavier when a subject has broad access, which is acceptable at the target scale. This is also available as the ground-truth path.
- **Milestone 2 — maintained reverse index.** `reverse_index` holds resolved *structural* grants: `(store, tenant, subject, permission, object_type, object_id, conditioned)`. ListObjects becomes one indexed scan plus a condition re-check only on rows flagged `conditioned`. The index is maintained incrementally inside the write transaction when tuples or attributes change (recomputing the affected closure), with a full-rebuild path. Conditioned grants are stored but flagged and never assumed, so request-time predicates remain correct.

### 7.4 Correctness backbone

A differential, property-based harness generates random schemas and tuple sets and asserts that the CTE path, the engine-driven oracle, and the reverse index agree for Check, ListObjects, and ListSubjects across thousands of cases. The fast paths are trusted only when the oracle agrees.

## 8. ABAC condition evaluation

Conditions are evaluated by a purpose-built, sandboxed, typed predicate evaluator with a fixed function and operator library: comparisons, `and`/`or`/`not`, `in`/`contains`, arithmetic, and date/time helpers. It is dependency-light, deterministic, and serializes cleanly for the DSL. Its surface is kept CEL-shaped so a future move to Google CEL is possible without changing stored schemas. Conditions are evaluated during Check (as conditioned tuples are reached) and during ListObjects (on rows flagged `conditioned`), reading from synced resource attributes, request context, and the tuple's stored parameters.

## 9. Caching and consistency

### 9.1 Caching

Caching is a pluggable seam: `ICacheStore` lives in `Relkit.Abstractions`. Two implementations ship:

- **In-process `MemoryCache`** — the default for a single instance.
- **Postgres-native `UNLOGGED`-table cache** — shared across instances with no additional infrastructure. `UNLOGGED` skips the WAL for fast writes and is rebuilt on miss after a restart. TTL is an `expires_at` column with lazy expiry on read and a periodic sweep.

The service and a horizontally scaled Blazor deployment therefore run on Postgres alone — tuples, attributes, reverse index, and cache in one store.

Layers: per-request memoization dedupes repeated sub-checks; a cross-request check cache is keyed by `(store, tenant, schema_version, object, permission, subject)` and stores only unconditioned results (attribute- and context-dependent results are recomputed). Invalidation uses a coarse per-`(store, tenant)` epoch bumped on any write and committed in the same transaction.

### 9.2 Consistency

Tuple writes, attribute syncs, reverse-index maintenance, and the cache-epoch bump occur in a single Postgres transaction, giving strong read-your-writes consistency. The system targets small-to-medium scale (per tenant: dozens to low-hundreds of users, low-thousands of resources, tens of thousands of tuples, low query rates) and relies on relational transaction guarantees as its complete consistency story.

## 10. Public API surface and error handling

### 10.1 Interfaces

```
IAuthorizer            // decision API (read)
  CheckAsync(req)            -> { Allowed, Explain? }
  BatchCheckAsync(reqs)      -> results[]
  ListObjectsAsync(req)      -> objectIds[]    (subject, type, permission, filter, paging)
  ListSubjectsAsync(req)     -> subjects[]

IRelationManager       // facts (write, transactional batches)
  WriteTuplesAsync / DeleteTuplesAsync
  WriteAttributesAsync       // sync authz-relevant resource fields
  ReadTuplesAsync            // admin / audit

ISchemaManager         // application-developer layer
  ValidateSchema / SetActiveSchema / GetSchema    (fluent builder or DSL)

IStoreManager / ITenantManager                     // provisioning
```

DI wiring: `services.AddRelkit().UsePostgres(conn).UseSchema(builder)`. Switching to the remote service is one registration change to `Relkit.Client`'s `IAuthorizer`; callers are unchanged.

### 10.2 Explain

`CheckAsync` with `Explain` returns the decision trace: which tuples and which branches (union, intersection, exclusion, conditions) produced the result. This powers a "test this permission" UI and support diagnostics, and is a first-class feature of a configurable permission system.

### 10.3 Error handling

The governing distinction is *deny* versus *error*: allow/deny is always a return value, never an exception; a malformed schema or an unknown type is an exception.

| Situation | Behaviour |
|---|---|
| Missing tuple / attribute / no grant | Deny (default-deny) |
| Unknown type/relation/permission in a request | Typed exception (caller bug, surfaced loudly) |
| Invalid schema at load | Fail fast with rich messages (dangling relations, non-terminating recursion, mistyped conditions); never surfaces at check time |
| Condition evaluation fails at runtime (missing attribute, type mismatch) | Configurable, default deny + diagnostic |
| Depth/cycle guard tripped | Deny + diagnostic + log |
| Cross-tenant / cross-store reference | Rejected |

## 11. Testing, roadmap, licensing, observability

### 11.1 Testing

- **Differential / property-based harness** — random schemas and tuples assert `CTE ≡ engine-driven oracle ≡ reverse index` for all read operations. The correctness backbone.
- **Conformance suite** — declarative cases (`schema + tuples + attributes → expected results`) in a portable format, doubling as documentation and as the bar any future storage provider must pass. The worked examples of Section 12 are named acceptance cases.
- **In-memory provider** for millisecond unit tests; **Testcontainers Postgres** for integration tests.
- **BenchmarkDotNet** suite tracking Check and ListObjects latency at representative scale.

### 11.2 Build roadmap

| Milestone | Delivers |
|---|---|
| **M0 — Engine core** | `Abstractions`, schema model, fluent builder, validation, in-memory provider, engine-driven traversal (oracle), all four operations, conformance and differential harness. |
| **M1 — Postgres + usable library** | Postgres provider, CTE primary path, transactional writes, epoch cache, on-the-fly ListObjects, ABAC conditions, `Explain`. The Blazor application adopts the engine here. |
| **M2 — Performance** | Maintained reverse index and Postgres-native `UNLOGGED` cache, diffed against the oracle. |
| **M3 — Service / AaaS** | `Relkit.Service` (gRPC + REST + OpenAPI), `Relkit.Client`, authn, multi-store, DSL parser, container image. |

### 11.3 Licensing

Apache-2.0: permissive for commercial and open-source use, with an explicit patent grant suited to infrastructure other businesses depend on.

### 11.4 Observability and packaging

OpenTelemetry throughout: structured logs, metrics (check latency, cache hit-rate, reverse-index maintenance lag), tracing spans, and the `Explain` trace as a first-class diagnostic. One NuGet package per project under semantic versioning; the service additionally as a container image.

## 12. Worked examples

All names below are tenant **instances** (data); no tenant name appears in the schema. Relation names are the application's actions.

### 12.1 Role grant over a resource category — "vets may record drug dispensing"

```
category:drugs#dispenser@group:vets#member
inventory_item:vaccine-X#category@category:drugs
group:vets#member@user:dr-smith
permission:  inventory_item.record_dispense = dispenser + category->record_dispense - blocked
```
Check `record_dispense` on `vaccine-X` for `dr-smith` resolves through the category grant.

### 12.2 Team grant over a curated set — "the macropods round may edit a hand-picked species list"

```
species:kangaroo#editor@group:macropods#member      # explicit, per curated species
species:wallaby#editor@group:macropods#member
group:macropods#member@user:alice
```

### 12.3 Site-scoped access — "the keepers team edits Sydney enclosures, not other sites"

```
site:sydney#can_edit@group:keepers#member
enclosure:KH1#site@site:sydney
permission:  enclosure.edit = can_edit + site->edit - blocked
```
Melbourne is untouched, so "cannot access other sites" follows automatically from the scoping.

### 12.4 Direct grant on one instance — "give one user full rights on EL-001"

```
animal:EL-001#can_manage@user:carol
```

### 12.5 Structural condition — "only quarantine-trained vets may access animals in a quarantine enclosure"

```
animal:EL-001#enclosure@enclosure:Q1
enclosure:Q1#in@group:quarantine
group:quarantine-trained#member@user:dr-smith
permission:  animal.access = base_access ∪ ( enclosure->quarantine_gate
                                              & (group:vets#member ∪ group:vet-nurses#member)
                                              & group:quarantine-trained#member )
```
Modelled with relationships and intersection so it remains indexable and listable.

### 12.6 Request-time condition — time-bounded dispensing

```
category:drugs#dispenser@group:vets#member with within_hours(start=8, end=18)
```
The condition is evaluated at request time against `context.now`.
