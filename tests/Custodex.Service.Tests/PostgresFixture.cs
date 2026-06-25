using Npgsql;
using Testcontainers.PostgreSql;

namespace Custodex.Service.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithEnvironment("POSTGRES_INITDB_ARGS",
                "--locale-provider=icu --icu-locale=en-US --encoding=UTF8 --locale=C.UTF-8")
            .Build();

    public string ConnectionString =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { SearchPath = "custodex" }.ToString();

    public string RawConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Custodex.Storage.Postgres.MigrationRunner.ApplyAsync(conn);
        await conn.CloseAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("service")]
public sealed class ServiceCollection : ICollectionFixture<PostgresFixture> { }
