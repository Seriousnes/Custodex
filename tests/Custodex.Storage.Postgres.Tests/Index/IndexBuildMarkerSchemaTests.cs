using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexBuildMarkerSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task Migration_creates_the_build_marker_table()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);

        var exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema='custodex' AND table_name='index_build_markers')");
        exists.ShouldBeTrue();
    }
}
