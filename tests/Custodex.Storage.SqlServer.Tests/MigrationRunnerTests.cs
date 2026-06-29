using Dapper;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

[Collection("sqlserver")]
public class MigrationRunnerTests(SqlServerFixture fx)
{
    private static readonly string[] ExpectedTables =
    [
        "stores", "schema_versions", "tenants", "relation_tuples",
        "object_attributes", "cache_entries",
        "change_log", "tenant_epochs", "schema_migrations",
        "reverse_index", "index_build_markers"
    ];

    [Fact]
    public async Task Apply_creates_every_table()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var tables = (await conn.QueryAsync<string>(
            "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'custodex'"))
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
            "SELECT i.name FROM sys.indexes i JOIN sys.objects o ON i.object_id = o.object_id " +
            "JOIN sys.schemas s ON o.schema_id = s.schema_id WHERE s.name = 'custodex' AND i.name IS NOT NULL"))
            .ToHashSet();

        indexes.ShouldContain("ix_relation_tuples_forward");
        indexes.ShouldContain("ix_relation_tuples_reverse");
        indexes.ShouldContain("ux_relation_tuples_natural");
        indexes.ShouldContain("ix_reverse_index_scan");
        indexes.ShouldContain("ux_reverse_index_natural");
    }

    [Fact]
    public async Task Identifier_columns_use_the_ordinal_binary_collation()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var collation = await conn.ExecuteScalarAsync<string>(
            "SELECT c.collation_name FROM sys.columns c " +
            "JOIN sys.objects o ON c.object_id = o.object_id " +
            "JOIN sys.schemas s ON o.schema_id = s.schema_id " +
            "WHERE s.name = 'custodex' AND o.name = 'relation_tuples' AND c.name = 'object_id'");
        collation.ShouldBe("Latin1_General_100_BIN2");
    }

    [Fact]
    public async Task Apply_is_idempotent_and_records_each_script_once()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await MigrationRunner.ApplyAsync(conn);

        var applied = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM custodex.schema_migrations WHERE version = '001_initial_schema'");
        applied.ShouldBe(1);
    }
}
