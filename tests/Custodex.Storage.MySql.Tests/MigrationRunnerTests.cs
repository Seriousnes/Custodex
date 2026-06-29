using Dapper;

using Shouldly;

namespace Custodex.Storage.MySql.Tests;

[Collection("mysql")]
public class MigrationRunnerTests(MySqlFixture fx)
{
    [Fact]
    public async Task Apply_is_idempotent_and_creates_the_core_tables()
    {
        await using var conn = await fx.OpenAsync();

        await MigrationRunner.ApplyAsync(conn);
        await MigrationRunner.ApplyAsync(conn);

        var applied = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM schema_migrations");
        applied.ShouldBe(1);

        foreach (var table in new[]
        {
            "stores", "schema_versions", "tenants", "relation_tuples",
            "object_attributes", "change_log", "tenant_epochs", "cache_entries",
        })
        {
            var exists = await conn.QuerySingleAsync<long>(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t",
                new { t = table });
            exists.ShouldBe(1, $"table '{table}' should exist after migration");
        }
    }

    [Fact]
    public async Task Relation_tuples_natural_key_is_unique()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var indexes = await conn.QuerySingleAsync<long>(
            """
            SELECT COUNT(*) FROM information_schema.statistics
            WHERE table_schema = DATABASE() AND table_name = 'relation_tuples'
              AND index_name = 'ux_relation_tuples_natural' AND non_unique = 0
            """);
        indexes.ShouldBeGreaterThan(0);
    }
}
