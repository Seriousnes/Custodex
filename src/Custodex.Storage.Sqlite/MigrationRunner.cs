using System.Reflection;

using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

/// <summary>
/// Applies embedded SQL migration scripts to a SQLite database in lexical order, recording each
/// applied script in a <c>schema_migrations</c> tracking table so repeated runs are idempotent
/// no-ops.
/// </summary>
public sealed class MigrationRunner
{
    private const string ResourcePrefix = "Custodex.Storage.Sqlite.Migrations.";

    /// <summary>
    /// Ensures the <c>schema_migrations</c> tracking table exists, then applies every embedded
    /// <c>Migrations/*.sql</c> script that has not yet been recorded, wrapping each script and its
    /// tracking row in a single transaction. A second call with the same connection applies nothing.
    /// </summary>
    /// <param name="connection">An open <see cref="SqliteConnection" /> to the target database.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await EnsurePragmasAsync(connection, ct);

        await EnsureTrackingTableAsync(connection, ct);

        var applied = await LoadAppliedVersionsAsync(connection, ct);

        foreach (var (version, sql) in LoadScripts())
        {
            if (applied.Contains(version))
                continue;

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

            await using (var scriptCmd = connection.CreateCommand())
            {
                scriptCmd.Transaction = tx;
                scriptCmd.CommandText = sql;
                await scriptCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var trackCmd = connection.CreateCommand())
            {
                trackCmd.Transaction = tx;
                trackCmd.CommandText = "INSERT INTO schema_migrations (version) VALUES ($v)";
                trackCmd.Parameters.AddWithValue("$v", version);
                await trackCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }

    private static async Task EnsurePragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureTrackingTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "version TEXT PRIMARY KEY, applied_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> LoadAppliedVersionsAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_migrations";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

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
