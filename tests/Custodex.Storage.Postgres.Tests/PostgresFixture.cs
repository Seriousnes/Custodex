using Npgsql;
using Testcontainers.PostgreSql;

namespace Custodex.Storage.Postgres.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    /// <summary>Gets the connection string for the running Postgres container.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync() => await _container.StartAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Opens and returns a new <see cref="NpgsqlConnection" /> to the container.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
