using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

internal static class SqliteConnections
{
    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await ApplyPragmasAsync(conn, ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public static async Task ApplyPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
