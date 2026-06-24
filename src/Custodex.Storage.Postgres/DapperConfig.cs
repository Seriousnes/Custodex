using System.Runtime.CompilerServices;

namespace Custodex.Storage.Postgres;

internal static class DapperConfig
{
    /// <summary>
    /// Enables underscore-to-PascalCase column mapping in Dapper so that snake_case column names
    /// (e.g. <c>object_type</c>, <c>occurred_at</c>) automatically fill PascalCase record fields
    /// without manual column aliases on every query.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Intentional: ensures Dapper underscore mapping is active for all store queries in this assembly.")]
    [ModuleInitializer]
    internal static void Init() => Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
}
