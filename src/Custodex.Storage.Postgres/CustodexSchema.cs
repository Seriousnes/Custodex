using Npgsql;

namespace Custodex.Storage.Postgres;

internal static class CustodexSchema
{
    public const string Name = "custodex";

    public static string Apply(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = Name }.ToString();
}
