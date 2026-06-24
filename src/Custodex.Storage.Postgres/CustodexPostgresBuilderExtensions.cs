using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres.Managers;

namespace Custodex.Storage.Postgres;

/// <summary>Extension methods on <see cref="CustodexBuilder"/> for registering the Postgres provider.</summary>
public static class CustodexPostgresBuilderExtensions
{
    /// <summary>
    /// Registers all Postgres-backed stores, the CTE authorizer, the managers, and the cache store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Condition evaluation:</b> conditions are evaluated by the engine's default
    /// <see cref="CelConditionEvaluator"/>; a failing or missing-attribute condition is
    /// default-deny. To substitute a custom evaluator, register an <see cref="IConditionEvaluator"/>
    /// on the service collection <i>before</i> calling <c>UsePostgres</c> — that registration
    /// takes precedence because this method uses <c>TryAddSingleton</c>.
    /// </para>
    /// <para>
    /// <b>Cross-request caching:</b> this registration does not enable cross-request result
    /// caching. Per-request memoization and read-your-writes consistency still apply, but
    /// previously computed authorization results are not reused across separate requests.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Custodex builder returned by <c>AddCustodex()</c>.</param>
    /// <param name="connectionString">Npgsql connection string for the Postgres database.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    public static CustodexBuilder UsePostgres(this CustodexBuilder builder, string connectionString)
    {
        var s = builder.Services;

        s.TryAddSingleton(new NpgsqlUnitOfWorkFactory(connectionString));
        s.TryAddSingleton<IUnitOfWorkFactory>(sp => sp.GetRequiredService<NpgsqlUnitOfWorkFactory>());

        s.TryAddSingleton(new NpgsqlRelationStore(connectionString));
        s.TryAddSingleton<IRelationStore>(sp => sp.GetRequiredService<NpgsqlRelationStore>());

        s.TryAddSingleton(new NpgsqlSchemaStore(connectionString));
        s.TryAddSingleton<ISchemaStore>(sp => sp.GetRequiredService<NpgsqlSchemaStore>());

        s.TryAddSingleton(new NpgsqlAttributeStore(connectionString));
        s.TryAddSingleton<IAttributeStore>(sp => sp.GetRequiredService<NpgsqlAttributeStore>());

        s.TryAddSingleton(new NpgsqlChangeLogStore(connectionString));
        s.TryAddSingleton<IChangeLogStore>(sp => sp.GetRequiredService<NpgsqlChangeLogStore>());

        s.TryAddSingleton<IConditionEvaluator, CelConditionEvaluator>();

        s.TryAddSingleton<ICacheStore>(_ => new PostgresCacheStore(connectionString, default));

        s.TryAddSingleton(sp => new AuditedWritePath(
            sp.GetRequiredService<NpgsqlRelationStore>(),
            sp.GetRequiredService<NpgsqlAttributeStore>(),
            sp.GetRequiredService<NpgsqlSchemaStore>(),
            sp.GetRequiredService<NpgsqlChangeLogStore>(),
            sp.GetRequiredService<ICacheStore>()));

        s.TryAddSingleton<IRelationManager>(sp => new CustodexRelationManager(
            sp.GetRequiredService<NpgsqlUnitOfWorkFactory>(),
            sp.GetRequiredService<NpgsqlRelationStore>(),
            sp.GetRequiredService<IAttributeStore>(),
            sp.GetRequiredService<NpgsqlChangeLogStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<ISchemaManager>(sp => new CustodexSchemaManager(
            sp.GetRequiredService<NpgsqlUnitOfWorkFactory>(),
            sp.GetRequiredService<NpgsqlSchemaStore>(),
            sp.GetRequiredService<AuditedWritePath>()));

        s.TryAddSingleton<IStoreManager>(_ => new CustodexStoreManager(connectionString));
        s.TryAddSingleton<ITenantManager>(_ => new CustodexTenantManager(connectionString));

        s.TryAddSingleton<NpgsqlCteAuthorizer>(sp => new NpgsqlCteAuthorizer(
            connectionString,
            sp.GetRequiredService<NpgsqlSchemaStore>(),
            sp.GetRequiredService<NpgsqlAttributeStore>(),
            sp.GetRequiredService<IConditionEvaluator>()));
        s.TryAddSingleton<IAuthorizer>(sp => sp.GetRequiredService<NpgsqlCteAuthorizer>());

        return builder;
    }
}
