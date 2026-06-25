# Custodex

The evaluation engine for [Custodex](https://github.com/Seriousnes/Custodex) — the runtime-configurable ReBAC + ABAC authorization engine. This package (id `Custodex`) holds the schema authoring API, the permission algebra, the condition evaluator, and the dependency-injection entry point. It depends only on `Custodex.Abstractions` and contains no database code; pair it with a storage provider such as [`Custodex.Storage.Postgres`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Storage.Postgres/README.md) or [`Custodex.Storage.InMemory`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Storage.InMemory/README.md).

## Authoring a schema

`SchemaBuilder` produces the canonical schema AST. A type declares **relations** (who can be linked to an object) and **permissions** (computed from relations through the algebra):

```csharp
using Custodex.Core;

var schema = new SchemaBuilder("v1")
    .Type("group", t => t
        .Relation("member", s => s.User().SubjectSet("group", "member")))
    .Type("folder", t => t
        .Relation("editor", s => s.User().SubjectSet("group", "member"))
        .Permission("edit", p => p.Relation("editor")))
    .Type("document", t => t
        .Relation("parent", s => s.Type("folder"))
        .Relation("owner", s => s.User())
        .Relation("editor", s => s.User())
        .Relation("blocked", s => s.User())
        .Permission("edit", p => p
            .Relation("owner")
            .Union(u => u.Relation("editor"))
            .Union(u => u.Arrow("parent", "edit"))
            .Exclude(x => x.Relation("blocked"))))
    .Build();
```

### Relation fillers

A relation declares which subject kinds may fill it:

- `s.User()` — any subject of type `user`.
- `s.Type("folder")` — subjects of another entity type (object-to-object links).
- `s.SubjectSet("group", "member")` — members of a group, written `group:eng#member`.
- `s.Wildcard("user")` — the `user:*` wildcard, granting every user.

### The permission algebra

`PermExprBuilder` composes a permission from these operators:

- **Relation** — `p.Relation("owner")` resolves a direct relation.
- **Union** — `.Union(u => …)` (or simply chaining terms) grants if *any* branch grants: `owner ∪ editor`.
- **Intersect** — `.Intersect(i => …)` grants only if *all* branches grant: `editor ∩ verified`.
- **Exclude** — `.Exclude(x => …)` subtracts: `editor − blocked`.
- **Arrow** — `.Arrow("parent", "edit")` traverses a relation and evaluates a permission on the target, giving inheritance such as "can edit a document if you can edit its folder".
- **Conditioned** — `.Conditioned("name")` gates a branch on an ABAC condition.

## Conditions (ABAC)

Conditions are typed predicates over resource attributes, request context, and invocation parameters, authored with `ConditionBodyBuilder` and gating a `Conditioned` permission branch:

```csharp
var schema = new SchemaBuilder("v1")
    .Type("record", t => t
        .Relation("viewer", s => s.User())
        .Permission("view", p => p.Relation("viewer").Conditioned("business_hours")))
    .Condition("business_hours", _ => { },
        b => b.And(
            b.Ge(b.Hour(b.Now()), b.Const(9L)),
            b.Lt(b.Hour(b.Now()), b.Const(17L))))
    .Build();
```

Evaluation runs through the `IConditionEvaluator` seam. `NullConditionEvaluator` always passes (pure ReBAC); `CelConditionEvaluator` evaluates the real predicate. A failed condition or a missing attribute is **default-deny with a diagnostic — never an exception**.

## Registering with DI

`AddCustodex()` begins registration and returns a builder a provider extends (for example `.UsePostgres(connectionString)`). `UseSchema(...)` captures a startup schema on the builder so the host can validate and activate it through `ISchemaManager`:

```csharp
services.AddCustodex()
    .UsePostgres(connectionString)
    .UseSchema(schema);
```

`AddCustodexInstrumentation()` wires the engine's `"Custodex"` `ActivitySource` and `Meter` into an OpenTelemetry pipeline for traces and metrics.

## Evaluation engine

`EngineDrivenAuthorizer` is the portable `IAuthorizer` implementation: a C# walk over the permission expression issuing batched lookups through the storage interfaces. It is the in-memory execution path and the reference oracle every storage provider is validated against. It enforces cycle and depth guards and memoizes within a request.

Evaluation is deterministic — ambient time enters only through `RequestContext.Now`. A `CachingAuthorizer` decorator can cache unconditioned results across requests, invalidated by a coarse per-`(store, tenant)` epoch.

## License

Apache-2.0.
