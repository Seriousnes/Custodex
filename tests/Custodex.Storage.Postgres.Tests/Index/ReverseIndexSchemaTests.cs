using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ReverseIndexSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task Reverse_index_has_the_natural_key_unique_index()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var indexes = (await conn.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'reverse_index'"))
            .ToHashSet();

        indexes.ShouldContain("ux_reverse_index_natural");
        indexes.ShouldContain("ix_reverse_index_scan");
    }

    [Fact]
    public async Task Natural_key_rejects_a_duplicate_row()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await conn.ExecuteAsync("INSERT INTO stores (id) VALUES ('idx') ON CONFLICT DO NOTHING");
        await conn.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES ('idx','t') ON CONFLICT DO NOTHING");

        const string insert = """
            INSERT INTO reverse_index
              (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
            VALUES ('idx','t','v1','user:alice','edit','doc','item-1', false)
            """;
        await conn.ExecuteAsync(insert);

        var ex = await Should.ThrowAsync<Npgsql.PostgresException>(() => conn.ExecuteAsync(
            insert.Replace("false", "true")));
        ex.SqlState.ShouldBe("23505");
    }
}
