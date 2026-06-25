using Npgsql;
using Testcontainers.PostgreSql;

namespace Custodex.Storage.Postgres.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithEnvironment("POSTGRES_INITDB_ARGS",
                "--locale-provider=icu --icu-locale=en-US --encoding=UTF8 --locale=C.UTF-8")
            .Build();

    /// <summary>Gets the connection string for the running Postgres container, scoped to the Custodex schema.</summary>
    public string ConnectionString =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { SearchPath = "custodex" }.ToString();

    /// <summary>Gets the raw container connection string, with no Custodex search_path — mirrors a consumer's own connection.</summary>
    public string RawConnectionString => _container.GetConnectionString();

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
