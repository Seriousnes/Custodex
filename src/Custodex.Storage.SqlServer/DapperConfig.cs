using System.Runtime.CompilerServices;

namespace Custodex.Storage.SqlServer;

internal static class DapperConfig
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Ensures Dapper underscore mapping is active for all store queries in this assembly.")]
    [ModuleInitializer]
    internal static void Init() => Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
}
