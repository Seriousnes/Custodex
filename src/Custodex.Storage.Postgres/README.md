# Custodex.Storage.Postgres

The PostgreSQL storage and evaluation provider for [Custodex](https://github.com/Seriousnes/Custodex) — the production path. It is built on Npgsql and Dapper (no EF Core) and persists every store, schema, tuple, attribute, and audit record in Postgres. Evaluation runs as recursive CTEs inside the database, accelerated by a maintained reverse index for `ListObjects`.

## Registration

Chain `.UsePostgres(connectionString)` after `AddCustodex()` (from the [`Custodex`](https://github.com/Seriousnes/Custodex/blob/main/src/Custodex.Core/README.md) engine package):

```csharp
services.AddCustodex().UsePostgres(connectionString);
```

This registers, for the connection string:

- `IAuthorizer` — the recursive-CTE evaluation path.
- `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IChangeLogStore`, `ICacheStore` — the Dapper-backed stores.
- `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager` — the write and provisioning surface, each writing through an audited, transactional path.
- `IConditionEvaluator` — defaults to `CelConditionEvaluator`, so a failing or missing-attribute condition is default-deny.
- `IUnitOfWork` / `IUnitOfWorkFactory` — enlisting Npgsql transactions.

To use a custom condition evaluator, register your `IConditionEvaluator` *before* calling `UsePostgres` — that registration wins, because `UsePostgres` only fills in defaults.

## Migrations

Apply the schema with the bundled idempotent runner before serving requests:

```csharp
using Custodex.Storage.Postgres;

await using var conn = new Npgsql.NpgsqlConnection(connectionString);
await conn.OpenAsync();
await MigrationRunner.ApplyAsync(conn);
```

The migrations create the relationship tuples, object attributes, schema versions, stores, tenants, change log, reverse index, and an `UNLOGGED` cache table, all keyed by `(store, tenant)` for tenant isolation.

## Evaluation

`Check`, `ListObjects`, `ListSubjects`, and `BatchCheck` evaluate as recursive CTEs that walk the full permission algebra — union, intersection, exclusion, and arrow traversal — directly in Postgres. `ListObjects` reads from the reverse index, which is maintained in-transaction as tuples change, with conditioned rows re-checked at query time. The differential harness asserts that this path returns exactly what the portable engine-driven oracle returns.

Per-request memoization and read-your-writes consistency always hold. This registration does not enable cross-request result caching; previously computed decisions are not reused across separate requests.

## Requirements

A reachable PostgreSQL database. The provider's own integration tests run against Testcontainers and need Docker.

## License

Apache-2.0.
