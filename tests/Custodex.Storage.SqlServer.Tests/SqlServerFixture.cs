using Microsoft.Data.SqlClient;

using Testcontainers.MsSql;

namespace Custodex.Storage.SqlServer.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    /// <summary>Gets the connection string for the running SQL Server container, trusting the container's certificate.</summary>
    public string ConnectionString =>
        new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            TrustServerCertificate = true,
            Encrypt = false,
        }.ConnectionString;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var conn = await OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Opens and returns a new <see cref="SqlConnection" /> to the container.</summary>
    public async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture> { }
