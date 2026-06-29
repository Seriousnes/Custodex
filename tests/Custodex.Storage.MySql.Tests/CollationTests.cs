using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.MySql.Tests;

[Collection("mysql")]
public class CollationTests(MySqlFixture fx) : IAsyncLifetime
{
    private MySqlUnitOfWorkFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new MySqlUnitOfWorkFactory(fx.ConnectionString);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = MySqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT IGNORE INTO stores (id) VALUES (@s)",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT IGNORE INTO tenants (store_id, tenant_id) VALUES (@s, @t)",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Default_collation_is_non_ordinal_so_the_ordinal_collation_guards_are_meaningful()
    {
        await using var conn = await fx.OpenAsync();

        var defaultOrder = (await conn.QueryAsync<string>(
            "SELECT v FROM (SELECT 'Bravo' AS v UNION ALL SELECT 'Zulu' UNION ALL SELECT 'alpha') AS t ORDER BY v")).ToList();

        defaultOrder.ShouldBe(["alpha", "Bravo", "Zulu"]);
    }

    [Fact]
    public async Task Identifier_columns_are_ordinal_so_case_differs_and_listing_sorts_byte_wise()
    {
        var t = new TenantContext("collation", "tCase");
        await SeedTenantAsync(t);
        var store = new MySqlRelationStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t,
            [
                new RelationTuple(new EntityRef("res", "Alpha"), "editor", new SubjectRef("user", "x")),
                new RelationTuple(new EntityRef("res", "alpha"), "editor", new SubjectRef("user", "x")),
                new RelationTuple(new EntityRef("res", "*"), "editor", new SubjectRef("user", "x")),
            ], [], u);
            await u.CommitAsync();
        }

        var ids = await store.ListObjectIdsAsync(t, "res");
        ids.ShouldBe(["*", "Alpha", "alpha"]);

        (await store.GetByObjectAsync(t, new EntityRef("res", "Alpha"), "editor")).ShouldHaveSingleItem();
        (await store.GetByObjectAsync(t, new EntityRef("res", "alpha"), "editor")).ShouldHaveSingleItem();
        (await store.GetByObjectAsync(t, new EntityRef("res", "*"), "editor")).ShouldHaveSingleItem();
    }
}
