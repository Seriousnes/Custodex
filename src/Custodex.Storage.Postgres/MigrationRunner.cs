namespace Custodex.Storage.Postgres;

/// <summary>
/// Applies embedded SQL migration scripts to a Postgres database in lexical order,
/// recording each applied script in a <c>schema_migrations</c> tracking table so
/// repeated runs are idempotent no-ops.
/// </summary>
public sealed partial class MigrationRunner { }
