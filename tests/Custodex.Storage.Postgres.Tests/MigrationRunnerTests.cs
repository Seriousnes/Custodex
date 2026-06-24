using Dapper;
using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class MigrationRunnerTests(PostgresFixture fx)
{
    private static readonly string[] ExpectedTables =
    [
        "stores", "schema_versions", "tenants", "relation_tuples",
        "object_attributes", "reverse_index", "cache_entries",
        "change_log", "tenant_epochs", "schema_migrations"
    ];

    [Fact]
    public async Task Apply_creates_every_table()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"))
            .ToHashSet();

        foreach (var t in ExpectedTables)
            tables.ShouldContain(t);
    }

    [Fact]
    public async Task Apply_creates_forward_and_reverse_indexes()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var indexes = (await conn.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'"))
            .ToHashSet();

        indexes.ShouldContain("ix_relation_tuples_forward");
        indexes.ShouldContain("ix_relation_tuples_reverse");
        indexes.ShouldContain("ux_relation_tuples_natural");
        indexes.ShouldContain("ix_reverse_index_scan");
    }

    [Fact]
    public async Task Cache_entries_is_unlogged()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var persistence = await conn.ExecuteScalarAsync<string>(
            "SELECT relpersistence::text FROM pg_class WHERE relname = 'cache_entries'");
        persistence.ShouldBe("u");
    }

    [Fact]
    public async Task Apply_is_idempotent_and_records_each_script_once()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await MigrationRunner.ApplyAsync(conn);

        var applied = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM schema_migrations WHERE version = '001_initial_schema'");
        applied.ShouldBe(1);
    }
}
