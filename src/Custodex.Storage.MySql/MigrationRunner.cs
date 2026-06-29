using System.Reflection;

using MySqlConnector;

namespace Custodex.Storage.MySql;

/// <summary>
/// Applies embedded SQL migration scripts to a MySQL database in lexical order,
/// recording each applied script in a <c>schema_migrations</c> tracking table so
/// repeated runs are idempotent no-ops. Each script's statements are pure DDL using
/// <c>CREATE TABLE IF NOT EXISTS</c>; because MySQL implicitly commits DDL, a re-run after a
/// partial failure re-applies the remaining statements cleanly.
/// </summary>
public static class MigrationRunner
{
    private const string ResourcePrefix = "Custodex.Storage.MySql.Migrations.";

    /// <summary>
    /// Ensures the <c>schema_migrations</c> tracking table exists, then applies every
    /// embedded <c>Migrations/*.sql</c> script that has not yet been recorded, inserting the
    /// tracking row after the script's statements succeed. A second call with the same
    /// connection applies nothing.
    /// </summary>
    /// <param name="connection">An open <see cref="MySqlConnection" /> to the target database.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ApplyAsync(MySqlConnection connection, CancellationToken ct = default)
    {
        await EnsureTrackingTableAsync(connection, ct);

        var applied = await LoadAppliedVersionsAsync(connection, ct);

        foreach (var (version, sql) in LoadScripts())
        {
            if (applied.Contains(version))
                continue;

            foreach (var statement in SplitStatements(sql))
            {
                await using var scriptCmd = new MySqlCommand(statement, connection);
                await scriptCmd.ExecuteNonQueryAsync(ct);
            }

            await using var trackCmd = new MySqlCommand(
                "INSERT INTO schema_migrations (version) VALUES (@v)", connection);
            trackCmd.Parameters.AddWithValue("v", version);
            await trackCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureTrackingTableAsync(MySqlConnection connection, CancellationToken ct)
    {
        const string ddl =
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "version varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, " +
            "applied_at datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6), " +
            "PRIMARY KEY (version)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4";
        await using var cmd = new MySqlCommand(ddl, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> LoadAppliedVersionsAsync(MySqlConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new MySqlCommand("SELECT version FROM schema_migrations", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    private static IEnumerable<string> SplitStatements(string sql) =>
        sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(s => s.Length > 0);

    private static IEnumerable<(string Version, string Sql)> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Version: VersionFromResourceName(n), ResourceName: n))
            .OrderBy(x => x.Version, StringComparer.Ordinal)
            .Select(x => (x.Version, Sql: ReadResource(assembly, x.ResourceName)))];
    }

    private static string VersionFromResourceName(string resourceName)
    {
        var tail = resourceName[ResourcePrefix.Length..];
        return tail[..^".sql".Length];
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded migration: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
