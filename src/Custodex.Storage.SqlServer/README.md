# Custodex.Storage.SqlServer

The SQL Server storage and evaluation provider for [Custodex](https://github.com/Seriousnes/Custodex). It is built on Microsoft.Data.SqlClient and Dapper (no EF Core) and persists every store, schema, tuple, attribute, and audit record in SQL Server. Subject-set reachability is expanded with a recursive CTE inside the database; the permission algebra runs over those edges with the same semantics as the portable engine.

## Registration

Chain `.UseSqlServer(connectionString)` after `AddCustodex()` (from the [`Custodex`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Core/README.md) engine package):

```csharp
services.AddCustodex().UseSqlServer(connectionString);
```

This registers, for the connection string:

- `IAuthorizer` — the recursive-CTE evaluation path.
- `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IChangeLogStore`, `ICacheStore` — the Dapper-backed stores.
- `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager` — the write and provisioning surface, each writing through an audited, transactional path.
- `IConditionEvaluator` — defaults to `CelConditionEvaluator`, so a failing or missing-attribute condition is default-deny.
- `IUnitOfWork` / `IUnitOfWorkFactory` — enlisting SQL Server transactions.

To use a custom condition evaluator, register your `IConditionEvaluator` *before* calling `UseSqlServer` — that registration wins, because `UseSqlServer` only fills in defaults.

## Migrations

Apply the schema with the bundled idempotent runner before serving requests:

```csharp
using Custodex.Storage.SqlServer;

await using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
await conn.OpenAsync();
await MigrationRunner.ApplyAsync(conn);
```

The migrations create the relationship tuples, object attributes, schema versions, stores, tenants, change log, a cache table, and a tenant-epoch counter, all in a dedicated `custodex` schema and keyed by `(store, tenant)` for tenant isolation. Every identifier column uses the `Latin1_General_100_BIN2` collation so equality and ordering are byte-ordinal, matching the engine's ordinal comparison regardless of the database's default collation.

## Evaluation

`Check`, `ListObjects`, `ListSubjects`, and `BatchCheck` walk the full permission algebra — union, intersection, exclusion, and arrow traversal — over edges read from SQL Server, with nested subject-set membership expanded by a cycle-terminating recursive CTE. The differential harness asserts that this path returns exactly what the portable engine-driven oracle returns.

Per-request memoization and read-your-writes consistency always hold. This registration does not enable cross-request result caching; previously computed decisions are not reused across separate requests.

## Requirements

A reachable SQL Server database. The provider's own integration tests run against Testcontainers and need Docker.

## License

Apache-2.0.
