using System.Reflection;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Applies embedded SQL migration scripts to a SQL Server database in lexical order,
/// recording each applied script in a <c>schema_migrations</c> tracking table so
/// repeated runs are idempotent no-ops.
/// </summary>
public static class MigrationRunner
{
    private const string ResourcePrefix = "Custodex.Storage.SqlServer.Migrations.";

    /// <summary>
    /// Ensures the <c>custodex</c> schema and the <c>schema_migrations</c> tracking table exist, then
    /// applies every embedded <c>Migrations/*.sql</c> script that has not yet been recorded, wrapping
    /// each script and its tracking row in a single transaction. A second call with the same
    /// connection applies nothing.
    /// </summary>
    /// <param name="connection">An open <see cref="SqlConnection" /> to the target database.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ApplyAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(connection, ct);

        await EnsureTrackingTableAsync(connection, ct);

        var applied = await LoadAppliedVersionsAsync(connection, ct);

        foreach (var (version, sql) in LoadScripts())
        {
            if (applied.Contains(version))
                continue;

            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);

            await using (var scriptCmd = new SqlCommand(sql, connection, tx))
                await scriptCmd.ExecuteNonQueryAsync(ct);

            await using (var trackCmd = new SqlCommand(
                "INSERT INTO custodex.schema_migrations (version) VALUES (@v)", connection, tx))
            {
                trackCmd.Parameters.AddWithValue("@v", version);
                await trackCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }

    private static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken ct)
    {
        const string ddl =
            "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'custodex') " +
            "EXEC('CREATE SCHEMA custodex');";
        await using var cmd = new SqlCommand(ddl, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureTrackingTableAsync(SqlConnection connection, CancellationToken ct)
    {
        const string ddl =
            "IF OBJECT_ID(N'custodex.schema_migrations', N'U') IS NULL " +
            "CREATE TABLE custodex.schema_migrations (" +
            "version nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL " +
            "CONSTRAINT pk_schema_migrations PRIMARY KEY, " +
            "applied_at datetime2 NOT NULL CONSTRAINT df_schema_migrations_applied DEFAULT SYSUTCDATETIME());";
        await using var cmd = new SqlCommand(ddl, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> LoadAppliedVersionsAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new SqlCommand("SELECT version FROM custodex.schema_migrations", connection);
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
