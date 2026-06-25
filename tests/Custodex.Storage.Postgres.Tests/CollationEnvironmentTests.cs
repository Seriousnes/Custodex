using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class CollationEnvironmentTests(PostgresFixture fx)
{
    [Fact]
    public async Task Default_collation_is_non_ordinal_so_the_ordinal_collation_guards_are_meaningful()
    {
        await using var conn = await fx.OpenAsync();

        var defaultOrder = (await conn.QueryAsync<string>(
            "SELECT v FROM (VALUES ('Bravo'), ('Zulu'), ('alpha')) AS t(v) ORDER BY v")).ToList();

        defaultOrder.ShouldBe(["alpha", "Bravo", "Zulu"]);
    }
}
