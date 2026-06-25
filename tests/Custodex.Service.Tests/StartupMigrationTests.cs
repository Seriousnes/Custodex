using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Custodex.Service.Tests;

public sealed class StartupMigrationTests
{
    [Fact]
    public async Task Boot_against_clean_container_health_returns_ok()
    {
        await using var container = new Testcontainers.PostgreSql.PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithEnvironment("POSTGRES_INITDB_ARGS",
                "--locale-provider=icu --icu-locale=en-US --encoding=UTF8 --locale=C.UTF-8")
            .Build();

        await container.StartAsync();
        var connStr = new Npgsql.NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            SearchPath = "custodex",
        }.ToString();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", connStr);
            b.UseEnvironment("Development");
        });

        var client = factory.CreateClient();
        var resp = await client.GetAsync("/health");

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Provision_store_after_startup_migrations_succeeds()
    {
        await using var container = new Testcontainers.PostgreSql.PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .WithEnvironment("POSTGRES_INITDB_ARGS",
                "--locale-provider=icu --icu-locale=en-US --encoding=UTF8 --locale=C.UTF-8")
            .Build();

        await container.StartAsync();
        var connStr = new Npgsql.NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            SearchPath = "custodex",
        }.ToString();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", connStr);
            b.UseEnvironment("Development");
        });

        var scope = factory.Services.CreateScope();
        var stores = scope.ServiceProvider.GetRequiredService<Custodex.Abstractions.IStoreManager>();

        var ex = await Record.ExceptionAsync(() => stores.CreateStoreAsync("startup-test"));
        ex.ShouldBeNull();
    }
}
