using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests;

public class OrdinalSanityTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private readonly SqliteUnitOfWorkFactory _factory = new(fx.ConnectionString);

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = SqliteUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Ids_differing_only_by_case_are_distinct_and_list_is_ordinal_sorted()
    {
        var t = new TenantContext("ordinal", "tO");
        await SeedTenantAsync(t);
        var store = new SqliteRelationStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t,
            [
                new RelationTuple(new EntityRef("resource", "Bravo"), "editor", new SubjectRef("user", "x")),
                new RelationTuple(new EntityRef("resource", "bravo"), "editor", new SubjectRef("user", "x")),
                new RelationTuple(new EntityRef("resource", "alpha"), "editor", new SubjectRef("user", "x")),
                new RelationTuple(new EntityRef("resource", "*"), "editor", new SubjectRef("user", "x")),
            ], [], u);
            await u.CommitAsync();
        }

        var ids = await store.ListObjectIdsAsync(t, "resource");

        ids.ShouldBe(["*", "Bravo", "alpha", "bravo"]);
        ids.ShouldContain("Bravo");
        ids.ShouldContain("bravo");
    }

    [Fact]
    public async Task Case_distinct_object_is_isolated_on_lookup()
    {
        var t = new TenantContext("ordinal", "tO2");
        await SeedTenantAsync(t);
        var store = new SqliteRelationStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t,
            [
                new RelationTuple(new EntityRef("resource", "Casing"), "editor", new SubjectRef("user", "x")),
            ], [], u);
            await u.CommitAsync();
        }

        (await store.GetByObjectAsync(t, new EntityRef("resource", "casing"), "editor")).ShouldBeEmpty();
        (await store.GetByObjectAsync(t, new EntityRef("resource", "Casing"), "editor")).ShouldHaveSingleItem();
    }
}
