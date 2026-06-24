using System.Reflection;
using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Applies embedded SQL migration scripts to a Postgres database in lexical order,
/// recording each applied script in a <c>schema_migrations</c> tracking table so
/// repeated runs are idempotent no-ops.
/// </summary>
public sealed partial class MigrationRunner
{
    private const string ResourcePrefix = "Custodex.Storage.Postgres.Migrations.";

    /// <summary>
    /// Ensures the <c>schema_migrations</c> tracking table exists, then applies every
    /// embedded <c>Migrations/*.sql</c> script that has not yet been recorded, wrapping
    /// each script and its tracking row in a single transaction.  A second call with the
    /// same connection applies nothing.
    /// </summary>
    /// <param name="connection">An open <see cref="NpgsqlConnection" /> to the target database.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ApplyAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        await EnsureTrackingTableAsync(connection, ct);

        var applied = await LoadAppliedVersionsAsync(connection, ct);

        foreach (var (version, sql) in LoadScripts())
        {
            if (applied.Contains(version))
                continue;

            await using var tx = await connection.BeginTransactionAsync(ct);

            await using (var scriptCmd = new NpgsqlCommand(sql, connection, tx))
                await scriptCmd.ExecuteNonQueryAsync(ct);

            await using (var trackCmd = new NpgsqlCommand(
                "INSERT INTO schema_migrations (version) VALUES (@v)", connection, tx))
            {
                trackCmd.Parameters.AddWithValue("v", version);
                await trackCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }

    private static async Task EnsureTrackingTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        const string ddl =
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())";
        await using var cmd = new NpgsqlCommand(ddl, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> LoadAppliedVersionsAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT version FROM schema_migrations", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    private static IEnumerable<(string Version, string Sql)> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Version: VersionFromResourceName(n), ResourceName: n))
            .OrderBy(x => x.Version, StringComparer.Ordinal)
            .Select(x => (x.Version, Sql: ReadResource(assembly, x.ResourceName)))
            .ToList();
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
