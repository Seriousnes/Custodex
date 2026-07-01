using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Caching;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.MySql.Managers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Custodex.Storage.MySql;

/// <summary>Extension methods on <see cref="CustodexBuilder"/> for registering the MySQL provider.</summary>
public static class CustodexMySqlBuilderExtensions
{
    /// <summary>
    /// Registers all MySQL-backed stores, the portable engine-driven authorizer, the managers,
    /// and the cache store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Evaluation:</b> <see cref="IAuthorizer"/> resolves to the engine's portable
    /// <see cref="EngineDrivenAuthorizer"/>, which walks the permission algebra in C# over the
    /// MySQL-backed stores. The same decision semantics run on every storage provider.
    /// </para>
    /// <para>
    /// <b>Condition evaluation:</b> conditions are evaluated by the engine's default
    /// <see cref="CelConditionEvaluator"/>; a failing or missing-attribute condition is
    /// default-deny. To substitute a custom evaluator, register an <see cref="IConditionEvaluator"/>
    /// on the service collection <i>before</i> calling <c>UseMySql</c> — that registration
    /// takes precedence because this method uses <c>TryAddSingleton</c>.
    /// </para>
    /// <para>
    /// <b>Cross-request caching:</b> this registration does not enable cross-request result
    /// caching. Per-request memoization and read-your-writes consistency still apply, but
    /// previously computed authorization results are not reused across separate requests.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Custodex builder returned by <c>AddCustodex()</c>.</param>
    /// <param name="connectionString">MySqlConnector connection string for the MySQL database.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    public static CustodexBuilder UseMySql(this CustodexBuilder builder, string connectionString)
    {
        var s = builder.Services;

        s.TryAddSingleton(new MySqlUnitOfWorkFactory(connectionString));
        s.TryAddSingleton<IUnitOfWorkFactory>(sp => sp.GetRequiredService<MySqlUnitOfWorkFactory>());

        s.TryAddSingleton(new MySqlRelationStore(connectionString));
        s.TryAddSingleton<IRelationStore>(sp => sp.GetRequiredService<MySqlRelationStore>());

        s.TryAddSingleton(new MySqlSchemaStore(connectionString));
        s.TryAddSingleton<ISchemaStore>(sp => sp.GetRequiredService<MySqlSchemaStore>());

        s.TryAddSingleton(new MySqlAttributeStore(connectionString));
        s.TryAddSingleton<IAttributeStore>(sp => sp.GetRequiredService<MySqlAttributeStore>());

        s.TryAddSingleton(new MySqlChangeLogStore(connectionString));
        s.TryAddSingleton<IChangeLogStore>(sp => sp.GetRequiredService<MySqlChangeLogStore>());

        s.TryAddSingleton<IConditionEvaluator, CelConditionEvaluator>();

        s.TryAddSingleton<ICacheStore>(_ => new MySqlCacheStore(connectionString, default));

        s.TryAddSingleton(sp => new AuditedWritePath(
            sp.GetRequiredService<MySqlRelationStore>(),
            sp.GetRequiredService<MySqlAttributeStore>(),
            sp.GetRequiredService<MySqlSchemaStore>(),
            sp.GetRequiredService<MySqlChangeLogStore>(),
            sp.GetRequiredService<ICacheStore>()));

        s.TryAddSingleton<IRelationManager>(sp => new CustodexRelationManager(
            sp.GetRequiredService<MySqlUnitOfWorkFactory>(),
            sp.GetRequiredService<MySqlRelationStore>(),
            sp.GetRequiredService<IAttributeStore>(),
            sp.GetRequiredService<MySqlChangeLogStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<ISchemaManager>(sp => new CustodexSchemaManager(
            sp.GetRequiredService<MySqlUnitOfWorkFactory>(),
            sp.GetRequiredService<MySqlSchemaStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<IStoreManager>(_ => new CustodexStoreManager(connectionString));
        s.TryAddSingleton<ITenantManager>(_ => new CustodexTenantManager(connectionString));

        s.TryAddSingleton<IAuthorizer>(sp => new EngineDrivenAuthorizer(
            sp.GetRequiredService<ISchemaStore>(),
            sp.GetRequiredService<IRelationStore>(),
            sp.GetRequiredService<IAttributeStore>(),
            sp.GetRequiredService<IConditionEvaluator>()));

        s.AddCustodexDecisionCache();

        return builder;
    }
}
