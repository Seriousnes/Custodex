using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.Sqlite.Managers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Custodex.Storage.Sqlite;

/// <summary>Extension methods on <see cref="CustodexBuilder"/> for registering the SQLite provider.</summary>
public static class CustodexSqliteBuilderExtensions
{
    /// <summary>
    /// Registers all SQLite-backed stores, the portable engine-driven authorizer, the managers, and
    /// the cache store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Evaluation path:</b> the SQLite provider evaluates through the portable
    /// <see cref="EngineDrivenAuthorizer"/>, a C# walk over the permission algebra that issues batched
    /// indexed lookups through the registered stores.
    /// </para>
    /// <para>
    /// <b>Condition evaluation:</b> conditions are evaluated by the engine's default
    /// <see cref="CelConditionEvaluator"/>; a failing or missing-attribute condition is default-deny.
    /// To substitute a custom evaluator, register an <see cref="IConditionEvaluator"/> on the service
    /// collection <i>before</i> calling <c>UseSqlite</c> — that registration takes precedence because
    /// this method uses <c>TryAddSingleton</c>.
    /// </para>
    /// <para>
    /// <b>Cross-request caching:</b> this registration does not enable cross-request result caching.
    /// Per-request memoization and read-your-writes consistency still apply, but previously computed
    /// authorization results are not reused across separate requests.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Custodex builder returned by <c>AddCustodex()</c>.</param>
    /// <param name="connectionString">Microsoft.Data.Sqlite connection string for the SQLite database.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    public static CustodexBuilder UseSqlite(this CustodexBuilder builder, string connectionString)
    {
        var s = builder.Services;

        s.TryAddSingleton(new SqliteUnitOfWorkFactory(connectionString));
        s.TryAddSingleton<IUnitOfWorkFactory>(sp => sp.GetRequiredService<SqliteUnitOfWorkFactory>());

        s.TryAddSingleton(new SqliteRelationStore(connectionString));
        s.TryAddSingleton<IRelationStore>(sp => sp.GetRequiredService<SqliteRelationStore>());

        s.TryAddSingleton(new SqliteSchemaStore(connectionString));
        s.TryAddSingleton<ISchemaStore>(sp => sp.GetRequiredService<SqliteSchemaStore>());

        s.TryAddSingleton(new SqliteAttributeStore(connectionString));
        s.TryAddSingleton<IAttributeStore>(sp => sp.GetRequiredService<SqliteAttributeStore>());

        s.TryAddSingleton(new SqliteChangeLogStore(connectionString));
        s.TryAddSingleton<IChangeLogStore>(sp => sp.GetRequiredService<SqliteChangeLogStore>());

        s.TryAddSingleton<IConditionEvaluator, CelConditionEvaluator>();

        s.TryAddSingleton<ICacheStore>(_ => new SqliteCacheStore(connectionString, default));

        s.TryAddSingleton(sp => new AuditedWritePath(
            sp.GetRequiredService<SqliteRelationStore>(),
            sp.GetRequiredService<SqliteAttributeStore>(),
            sp.GetRequiredService<SqliteSchemaStore>(),
            sp.GetRequiredService<SqliteChangeLogStore>(),
            sp.GetRequiredService<ICacheStore>()));

        s.TryAddSingleton<IRelationManager>(sp => new CustodexRelationManager(
            sp.GetRequiredService<SqliteUnitOfWorkFactory>(),
            sp.GetRequiredService<SqliteRelationStore>(),
            sp.GetRequiredService<IAttributeStore>(),
            sp.GetRequiredService<SqliteChangeLogStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<ISchemaManager>(sp => new CustodexSchemaManager(
            sp.GetRequiredService<SqliteUnitOfWorkFactory>(),
            sp.GetRequiredService<SqliteSchemaStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<IStoreManager>(_ => new CustodexStoreManager(connectionString));
        s.TryAddSingleton<ITenantManager>(_ => new CustodexTenantManager(connectionString));

        s.TryAddSingleton<EngineDrivenAuthorizer>(sp => new EngineDrivenAuthorizer(
            sp.GetRequiredService<SqliteSchemaStore>(),
            sp.GetRequiredService<SqliteRelationStore>(),
            sp.GetRequiredService<SqliteAttributeStore>(),
            sp.GetRequiredService<IConditionEvaluator>()));
        s.TryAddSingleton<IAuthorizer>(sp => sp.GetRequiredService<EngineDrivenAuthorizer>());

        return builder;
    }
}
