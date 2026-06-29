# Custodex.Storage.Sqlite

The SQLite storage provider for [Custodex](https://github.com/Seriousnes/Custodex). It is built on Microsoft.Data.Sqlite and Dapper (no EF Core) and persists every store, schema, tuple, attribute, and audit record in an embedded SQLite database. Evaluation runs through the portable engine-driven authorizer, a C# walk over the permission algebra that issues batched indexed lookups through the stores — no external database server is required.

## Registration

Chain `.UseSqlite(connectionString)` after `AddCustodex()` (from the [`Custodex`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Core/README.md) engine package):

```csharp
services.AddCustodex().UseSqlite("Data Source=custodex.db");
```

This registers, for the connection string:

- `IAuthorizer` — the portable engine-driven evaluation path.
- `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IChangeLogStore`, `ICacheStore` — the Dapper-backed stores.
- `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager` — the write and provisioning surface, each writing through an audited, transactional path.
- `IConditionEvaluator` — defaults to `CelConditionEvaluator`, so a failing or missing-attribute condition is default-deny.
- `IUnitOfWork` / `IUnitOfWorkFactory` — enlisting SQLite transactions.

To use a custom condition evaluator, register your `IConditionEvaluator` *before* calling `UseSqlite` — that registration wins, because `UseSqlite` only fills in defaults.

## Migrations

Apply the schema with the bundled idempotent runner before serving requests:

```csharp
using Custodex.Storage.Sqlite;
using Microsoft.Data.Sqlite;

await using var conn = new SqliteConnection("Data Source=custodex.db");
await conn.OpenAsync();
await MigrationRunner.ApplyAsync(conn);
```

The migrations create the relationship tuples, object attributes, schema versions, stores, tenants, change log, tenant epochs, and a cache table, all keyed by `(store, tenant)` for tenant isolation. SQLite's default `BINARY` collation gives the ordinal identifier comparison the engine relies on.

## Evaluation

`Check`, `ListObjects`, `ListSubjects`, and `BatchCheck` evaluate through the portable engine-driven authorizer over the SQLite stores. The full permission algebra — union, intersection, exclusion, and arrow traversal — plus conditions runs in C# with the same semantics on any storage provider.

Per-request memoization and read-your-writes consistency always hold. This registration does not enable cross-request result caching; previously computed decisions are not reused across separate requests.

## Requirements

A writable path for the SQLite database file (or a shared in-memory connection). No external server and no Docker are required.

## License

Apache-2.0.
