using Dapper;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests;

public class MigrationRunnerTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    [Fact]
    public async Task Apply_is_idempotent_and_records_each_version_once()
    {
        await using var conn = await fx.OpenAsync();

        await MigrationRunner.ApplyAsync(conn);
        await MigrationRunner.ApplyAsync(conn);

        var applied = (await conn.QueryAsync<string>("SELECT version FROM schema_migrations")).ToList();
        applied.ShouldContain("001_initial_schema");
        applied.Count.ShouldBe(applied.Distinct().Count());
    }

    [Fact]
    public async Task Migration_creates_the_expected_tables()
    {
        await using var conn = await fx.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[]
        {
            "stores", "tenants", "schema_versions", "relation_tuples",
            "object_attributes", "change_log", "tenant_epochs", "cache_entries",
        })
            tables.ShouldContain(expected);
    }
}
