# Custodex

Custodex is a runtime-configurable **relationship-based (ReBAC) + attribute-based (ABAC) authorization engine** for .NET, modelled on Google Zanzibar (the lineage behind SpiceDB, OpenFGA, and Permify). It carries **zero domain concepts**: your application supplies its permission model as a *schema* and its authorization data as *tuples* and *attributes*. Custodex then answers four questions:

- **Check** — may this subject use this permission on this object?
- **ListObjects** — which objects of a type may this subject act on?
- **ListSubjects** — who may act on this object?
- **BatchCheck** — many Checks in one round trip.

Use it as an embedded library that evaluates in-process, or run it behind a gRPC/REST service as a standalone authorization service — the same engine either way.

## Getting started

### Install

Custodex packages are published to GitHub Packages, and public NuGet.org distribution is the intended home. To pull from GitHub Packages, place a `nuget.config` beside your solution:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="github" value="https://nuget.pkg.github.com/Seriousnes/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="YOUR_GITHUB_PAT" />
    </github>
  </packageSourceCredentials>
</configuration>
```

The token is a GitHub personal access token with the `read:packages` scope. Then add the packages for your scenario:

```bash
# Production: the Postgres-backed engine (pulls in the engine and contracts)
dotnet add package Custodex.Storage.Postgres

# Tests and local dev: the engine plus the database-free provider
dotnet add package Custodex
dotnet add package Custodex.Storage.InMemory
```

### Define a schema

Permissions are authored with the fluent `SchemaBuilder`. Here a `document` is editable by its owner or any editor:

```csharp
using Custodex.Core;

var schema = new SchemaBuilder("v1")
    .Type("document", t => t
        .Relation("owner", s => s.User())
        .Relation("editor", s => s.User())
        .Permission("edit", p => p.Relation("owner").Union(u => u.Relation("editor"))))
    .Build();
```

The full algebra — union, intersection, exclusion, arrow traversal, and conditions — is documented in the [`Custodex`](src/Custodex.Core/README.md) engine package.

### Run against Postgres

This is the production path: register the provider, apply migrations, provision the store and tenant, activate the schema, write a tuple, then Check.

```csharp
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.Postgres;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddCustodex().UsePostgres(connectionString);
var sp = services.BuildServiceProvider();

await using (var conn = new Npgsql.NpgsqlConnection(connectionString))
{
    await conn.OpenAsync();
    await MigrationRunner.ApplyAsync(conn);
}

var tenant = new TenantContext("store-1", "tenant-1");
await sp.GetRequiredService<IStoreManager>().CreateStoreAsync(tenant.Store);
await sp.GetRequiredService<ITenantManager>().CreateTenantAsync(tenant);
await sp.GetRequiredService<ISchemaManager>().SetActiveSchemaAsync(tenant.Store, schema);

await sp.GetRequiredService<IRelationManager>().WriteTuplesAsync(tenant, actor: "bootstrap",
[
    new RelationTuple(new EntityRef("document", "readme"), "owner", new SubjectRef("user", "alice")),
]);

var authorizer = sp.GetRequiredService<IAuthorizer>();
var result = await authorizer.CheckAsync(new CheckRequest(
    tenant,
    new EntityRef("document", "readme"),
    "edit",
    new SubjectRef("user", "alice"),
    new RequestContext(DateTimeOffset.UtcNow, new SubjectRef("user", "alice"),
        new Dictionary<string, object?>())));

Console.WriteLine(result.Allowed); // True
```

### Run in memory

For tests and demos, wire the engine over the in-memory stores directly — no DI, no migrations. The [`Custodex.Storage.InMemory`](src/Custodex.Storage.InMemory/README.md) package documents the full setup; the short version:

```csharp
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;

var schemaStore = new InMemorySchemaStore();
var relations = new InMemoryRelationStore();
var uow = new NoOpUnitOfWork();

await schemaStore.SetActiveAsync(tenant.Store, schema, uow);
await relations.WriteAsync(tenant,
[
    new RelationTuple(new EntityRef("document", "readme"), "owner", new SubjectRef("user", "alice")),
], [], uow);

var authorizer = new EngineDrivenAuthorizer(
    schemaStore, relations, new InMemoryAttributeStore(), new NullConditionEvaluator());
```

## How it works

Custodex keeps three concerns separate:

- **The engine** (this repository) — generic, with no knowledge of your domain.
- **The schema** — a versioned developer artifact describing entity types, relations, permissions, and conditions.
- **Tenant data** — tuples and attributes, editable at runtime with no code changes and no redeploy.

One set of evaluation semantics drives two execution paths that produce identical results: a portable engine-driven traversal (also the in-memory path) and Postgres recursive CTEs (the production path). A differential test harness generates random schemas and data and asserts the two stay in lock-step, so the fast path is trusted only when it agrees with the portable one.

Every store, schema, tuple, and attribute is scoped by `(store, tenant)`, so a single deployment serves many isolated tenants.

## Packages

| Package | What it is |
| --- | --- |
| [`Custodex.Abstractions`](src/Custodex.Abstractions/README.md) | The public contract — interfaces and records, no logic. Depend on it to code against contracts or to build a storage/condition provider. |
| [`Custodex`](src/Custodex.Core/README.md) | The evaluation engine: schema authoring, the permission algebra, conditions, and the DI entry point. |
| [`Custodex.Storage.InMemory`](src/Custodex.Storage.InMemory/README.md) | Database-free storage for tests, local development, and demos. |
| [`Custodex.Storage.Postgres`](src/Custodex.Storage.Postgres/README.md) | The PostgreSQL provider — the production storage and evaluation path. |

## Building from source

```bash
dotnet build Custodex.slnx
dotnet test  Custodex.slnx
```

Requires the .NET 10 SDK. The Postgres integration tests run against Testcontainers and need Docker.

## License

Apache-2.0.
