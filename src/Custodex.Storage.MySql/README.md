# Custodex.Storage.MySql

The MySQL storage provider for [Custodex](https://github.com/Seriousnes/Custodex). It is built on [MySqlConnector](https://mysqlconnector.net/) (MIT) and Dapper (no EF Core) and persists every store, schema, tuple, attribute, and audit record in MySQL. Evaluation runs through the engine's portable, conformance-proven `EngineDrivenAuthorizer`, which walks the full permission algebra in C# over the MySQL-backed stores.

## Registration

Chain `.UseMySql(connectionString)` after `AddCustodex()` (from the [`Custodex`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Core/README.md) engine package):

```csharp
services.AddCustodex().UseMySql(connectionString);
```

This registers, for the connection string:

- `IAuthorizer` — the portable engine-driven evaluation path.
- `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IChangeLogStore`, `ICacheStore` — the Dapper-backed stores.
- `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager` — the write and provisioning surface, each writing through an audited, transactional path.
- `IConditionEvaluator` — defaults to `CelConditionEvaluator`, so a failing or missing-attribute condition is default-deny.
- `IUnitOfWork` / `IUnitOfWorkFactory` — enlisting MySqlConnector transactions.

To use a custom condition evaluator, register your `IConditionEvaluator` *before* calling `UseMySql` — that registration wins, because `UseMySql` only fills in defaults.

## Migrations

Apply the schema with the bundled idempotent runner before serving requests:

```csharp
using Custodex.Storage.MySql;

await using var conn = new MySqlConnector.MySqlConnection(connectionString);
await conn.OpenAsync();
await MigrationRunner.ApplyAsync(conn);
```

The migrations create the relationship tuples, object attributes, schema versions, stores, tenants, change log, cache entries, and tenant-epoch tables, all keyed by `(store, tenant)` for tenant isolation. Identifier columns use the `utf8mb4_bin` collation so equality, `DISTINCT`, and ordering are byte-ordinal, matching the engine's ordinal comparison.

## Evaluation

`Check`, `ListObjects`, `ListSubjects`, and `BatchCheck` evaluate through `EngineDrivenAuthorizer`: a C# walk over the permission algebra — union, intersection, exclusion, and arrow traversal — issuing batched indexed lookups through the relation and attribute stores. Per-request memoization and read-your-writes consistency always hold. This registration does not enable cross-request result caching; previously computed decisions are not reused across separate requests.

## Requirements

A reachable MySQL 8 database. The provider's own integration tests run against Testcontainers and need Docker.

## License

Apache-2.0.
