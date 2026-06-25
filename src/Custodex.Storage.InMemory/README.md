# Custodex.Storage.InMemory

A database-free storage provider for [Custodex](https://github.com/Seriousnes/Custodex). Every storage seam is implemented with in-process collections, so the engine runs with no external dependencies — ideal for unit tests, local development, and demos. State lives for the lifetime of the process.

This package supplies the stores; the engine itself lives in the [`Custodex`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Core/README.md) package, which you also reference.

## What it provides

- `InMemorySchemaStore`, `InMemoryRelationStore`, `InMemoryAttributeStore`, `InMemoryChangeLogStore`, `InMemoryCacheStore` — implementations of the corresponding storage interfaces.
- `NoOpUnitOfWork` — the transaction seam as a no-op, since there is no database to enlist.

## Quickstart

Construct the stores, seed the schema and tuples, then hand them to `EngineDrivenAuthorizer`. There is no DI and no migration step. The `schema` here is authored with `SchemaBuilder` from the [`Custodex`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Core/README.md) engine package:

```csharp
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;

var tenant = new TenantContext("store-1", "tenant-1");
var uow = new NoOpUnitOfWork();

var schemaStore = new InMemorySchemaStore();
var relations = new InMemoryRelationStore();
var attributes = new InMemoryAttributeStore();

await schemaStore.SetActiveAsync(tenant.Store, schema, uow);
await relations.WriteAsync(tenant,
[
    new RelationTuple(new EntityRef("document", "readme"), "owner", new SubjectRef("user", "alice")),
], remove: [], uow);

var authorizer = new EngineDrivenAuthorizer(
    schemaStore, relations, attributes, new NullConditionEvaluator());

var result = await authorizer.CheckAsync(new CheckRequest(
    tenant,
    new EntityRef("document", "readme"),
    "edit",
    new SubjectRef("user", "alice"),
    new RequestContext(DateTimeOffset.UtcNow, new SubjectRef("user", "alice"),
        new Dictionary<string, object?>())));

Console.WriteLine(result.Allowed); // True
```

Swap `NullConditionEvaluator` for `CelConditionEvaluator` to evaluate ABAC conditions; pass attributes by seeding `InMemoryAttributeStore` with `SetAsync`.

Because these stores back the engine's reference evaluation, behaviour here matches the production Postgres provider exactly — the same schema and tuples yield the same decisions.

## License

Apache-2.0.
