using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite.Tests;

public sealed class SqliteFixture : IAsyncLifetime
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"custodex-sqlite-{Guid.NewGuid():N}.db");

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false,
        }.ToString();

        await using var conn = await OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.CompletedTask;
    }

    public async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }
}
