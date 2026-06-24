using Microsoft.Extensions.DependencyInjection;

namespace Custodex.Core;

/// <summary>Extension methods on <see cref="IServiceCollection"/> for registering Custodex.</summary>
public static class CustodexServiceCollectionExtensions
{
    /// <summary>
    /// Begins Custodex registration and returns a <see cref="CustodexBuilder"/> for further
    /// configuration. Chain a provider (e.g. <c>.UsePostgres(conn)</c>) and
    /// <c>.UseSchema(builder)</c> before calling <see cref="IServiceCollection"/> build.
    /// </summary>
    /// <param name="services">The service collection to register Custodex into.</param>
    /// <returns>A <see cref="CustodexBuilder"/> scoped to <paramref name="services"/>.</returns>
    public static CustodexBuilder AddCustodex(this IServiceCollection services) => new(services);
}
