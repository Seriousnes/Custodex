using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.SqlServer.Managers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Custodex.Storage.SqlServer;

/// <summary>Extension methods on <see cref="CustodexBuilder"/> for registering the SQL Server provider.</summary>
public static class CustodexSqlServerBuilderExtensions
{
    /// <summary>
    /// Registers all SQL Server-backed stores, the authorizer, the managers, and the cache store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Condition evaluation:</b> conditions are evaluated by the engine's default
    /// <see cref="CelConditionEvaluator"/>; a failing or missing-attribute condition is
    /// default-deny. To substitute a custom evaluator, register an <see cref="IConditionEvaluator"/>
    /// on the service collection <i>before</i> calling <c>UseSqlServer</c> — that registration
    /// takes precedence because this method uses <c>TryAddSingleton</c>.
    /// </para>
    /// <para>
    /// <b>Cross-request caching:</b> this registration does not enable cross-request result
    /// caching. Per-request memoization and read-your-writes consistency still apply, but
    /// previously computed authorization results are not reused across separate requests.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Custodex builder returned by <c>AddCustodex()</c>.</param>
    /// <param name="connectionString">Connection string for the SQL Server database.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    public static CustodexBuilder UseSqlServer(this CustodexBuilder builder, string connectionString)
    {
        var s = builder.Services;

        s.TryAddSingleton(new SqlServerUnitOfWorkFactory(connectionString));
        s.TryAddSingleton<IUnitOfWorkFactory>(sp => sp.GetRequiredService<SqlServerUnitOfWorkFactory>());

        s.TryAddSingleton(new SqlServerRelationStore(connectionString));
        s.TryAddSingleton<IRelationStore>(sp => sp.GetRequiredService<SqlServerRelationStore>());

        s.TryAddSingleton(new SqlServerSchemaStore(connectionString));
        s.TryAddSingleton<ISchemaStore>(sp => sp.GetRequiredService<SqlServerSchemaStore>());

        s.TryAddSingleton(new SqlServerAttributeStore(connectionString));
        s.TryAddSingleton<IAttributeStore>(sp => sp.GetRequiredService<SqlServerAttributeStore>());

        s.TryAddSingleton(new SqlServerChangeLogStore(connectionString));
        s.TryAddSingleton<IChangeLogStore>(sp => sp.GetRequiredService<SqlServerChangeLogStore>());

        s.TryAddSingleton<IConditionEvaluator, CelConditionEvaluator>();

        s.TryAddSingleton(new SqlServerCacheStoreFactory(connectionString));
        s.TryAddSingleton<ICacheStore>(sp => sp.GetRequiredService<SqlServerCacheStoreFactory>().For(default));

        s.TryAddSingleton(sp => new AuditedWritePath(
            sp.GetRequiredService<SqlServerRelationStore>(),
            sp.GetRequiredService<SqlServerAttributeStore>(),
            sp.GetRequiredService<SqlServerSchemaStore>(),
            sp.GetRequiredService<SqlServerChangeLogStore>(),
            sp.GetRequiredService<ICacheStore>()));

        s.TryAddSingleton<IRelationManager>(sp => new CustodexRelationManager(
            sp.GetRequiredService<SqlServerUnitOfWorkFactory>(),
            sp.GetRequiredService<SqlServerRelationStore>(),
            sp.GetRequiredService<IAttributeStore>(),
            sp.GetRequiredService<SqlServerChangeLogStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<ISchemaManager>(sp => new CustodexSchemaManager(
            sp.GetRequiredService<SqlServerUnitOfWorkFactory>(),
            sp.GetRequiredService<SqlServerSchemaStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<IStoreManager>(_ => new CustodexStoreManager(connectionString));
        s.TryAddSingleton<ITenantManager>(_ => new CustodexTenantManager(connectionString));

        s.TryAddSingleton<SqlServerCteAuthorizer>(sp => new SqlServerCteAuthorizer(
            connectionString,
            sp.GetRequiredService<SqlServerSchemaStore>(),
            sp.GetRequiredService<SqlServerAttributeStore>(),
            sp.GetRequiredService<IConditionEvaluator>()));
        s.TryAddSingleton<IAuthorizer>(sp => sp.GetRequiredService<SqlServerCteAuthorizer>());

        return builder;
    }
}
