# Custodex.Abstractions

The public contract for [Custodex](https://github.com/Seriousnes/Custodex) — interfaces and records only, no logic. Every other Custodex package depends on this one, and most applications pull it in transitively through the engine or a storage provider.

Reference this package directly when you want to:

- code against Custodex contracts without taking the engine, or
- implement your own storage or condition provider.

## What it contains

### Core value types

The vocabulary every operation speaks. References are `(Type, Id)` pairs; the id `"*"` is the reserved wildcard meaning every instance of a type.

- `EntityRef(Type, Id)` — an object, e.g. `document:readme`.
- `SubjectRef(Type, Id, Relation?)` — a subject; with a relation it is a subject set such as `group:eng#member`.
- `RelationTuple(Object, Relation, Subject, Condition?)` — one relationship fact.
- `ConditionRef(Name, Parameters)` — a condition invocation carried by a tuple.
- `TenantContext(Store, Tenant)` — the isolation scope carried by every operation, so a tuple value is reusable across stores.
- `RequestContext(Now, Subject, Attributes)` — per-request ambient state; `Now` is the only way time enters evaluation.

### Decision API

`IAuthorizer` is the consumer-facing decision surface and the seam a remote client substitutes:

```csharp
public interface IAuthorizer
{
    Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default);
    Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default);
    Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default);
}
```

The request and result records (`CheckRequest`/`CheckResult`, `BatchCheckRequest`/`CheckItem`, `ListObjectsRequest`/`ListObjectsResult`, `ListSubjectsRequest`/`ListSubjectsResult`) live here too. Setting `Explain = true` on a `CheckRequest` returns an `ExplainNode` tree describing why a decision was reached.

### Management API

The write and administration seams:

- `IRelationManager` — write, delete, and read tuples and attributes, plus read the audited change log.
- `ISchemaManager` — validate, activate, and read the schema for a store.
- `IStoreManager` / `ITenantManager` — provision stores and tenants.

Supporting records include `TupleFilter`, `ChangeLogFilter`, `ChangeLogEntry`, and `SchemaValidationResult`.

### Schema model

The canonical schema AST, a deliberately closed set the engine evaluates exhaustively:

- `Schema(Version, Types, Conditions)` with `EntityTypeDef`, `RelationDef`, `SubjectTypeRef`, and `PermissionDef`.
- The permission expression nodes: `RelationRef`, `Union`, `Intersect`, `Exclude`, `Arrow`, and `Conditioned` (all `PermExpr`).
- `ConditionDef`, `ConditionParam`, and the `ConditionType` enum.

### Storage and condition seams

Implement these to add a provider; the engine depends only on the interfaces:

- `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IChangeLogStore`, `ICacheStore`, `IIndexStore`.
- `IUnitOfWork` / `IUnitOfWorkFactory` — the transaction seam.
- `IConditionEvaluator` — the predicate-evaluation seam used for ABAC conditions.

### Exceptions

Allow and deny are always a `CheckResult`, never an exception. The exceptions here signal caller or schema errors: `SchemaValidationException`, `UnknownTypeException`, `UnknownRelationException`, `UnknownPermissionException`, `EvaluationLimitException` (a tripped depth or cycle guard), and `ExclusionCycleException` (a permission that re-entered itself through an exclusion, which has no sound decision).

## License

Apache-2.0.
