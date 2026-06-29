using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

[Collection("sqlserver")]
public class CollationTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);

    [Fact]
    public async Task Default_database_collation_is_non_ordinal_so_the_ordinal_column_collation_is_meaningful()
    {
        await using var conn = await fx.OpenAsync();
        var defaultOrder = (await conn.QueryAsync<string>(
            "SELECT v FROM (VALUES ('Bravo'), ('Zulu'), ('alpha')) AS t(v) ORDER BY v")).ToList();

        defaultOrder.ShouldBe(["alpha", "Bravo", "Zulu"]);
    }

    [Fact]
    public async Task Object_ids_differing_only_by_case_are_distinct_rows()
    {
        var t = new TenantContext("coll-case", "tCase");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerRelationStore(fx.ConnectionString);

        await using (var u = await Factory.BeginAsync())
        {
            await store.WriteAsync(t,
            [
                new RelationTuple(new EntityRef("resource", "Alpha"), "viewer", new SubjectRef("user", "u1")),
                new RelationTuple(new EntityRef("resource", "alpha"), "viewer", new SubjectRef("user", "u1")),
            ], [], u);
            await u.CommitAsync();
        }

        var upper = await store.GetByObjectAsync(t, new EntityRef("resource", "Alpha"), "viewer");
        upper.ShouldHaveSingleItem().Object.Id.ShouldBe("Alpha");

        var lower = await store.GetByObjectAsync(t, new EntityRef("resource", "alpha"), "viewer");
        lower.ShouldHaveSingleItem().Object.Id.ShouldBe("alpha");
    }

    [Fact]
    public async Task Subject_ids_differing_only_by_case_are_matched_case_sensitively()
    {
        var t = new TenantContext("coll-subj", "tSubj");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerRelationStore(fx.ConnectionString);

        await using (var u = await Factory.BeginAsync())
        {
            await store.WriteAsync(t,
            [
                new RelationTuple(new EntityRef("resource", "r1"), "viewer", new SubjectRef("user", "Bob")),
                new RelationTuple(new EntityRef("resource", "r1"), "viewer", new SubjectRef("user", "bob")),
            ], [], u);
            await u.CommitAsync();
        }

        var bob = await store.GetBySubjectAsync(t, new SubjectRef("user", "bob"));
        bob.ShouldHaveSingleItem().Subject.Id.ShouldBe("bob");
    }

    [Fact]
    public async Task Wildcard_subject_id_round_trips()
    {
        var t = new TenantContext("coll-wild", "tWild");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerRelationStore(fx.ConnectionString);

        await using (var u = await Factory.BeginAsync())
        {
            await store.WriteAsync(t,
            [
                new RelationTuple(new EntityRef("resource", "r1"), "viewer", new SubjectRef("user", "*")),
            ], [], u);
            await u.CommitAsync();
        }

        var got = await store.GetByObjectAsync(t, new EntityRef("resource", "r1"), "viewer");
        var tuple = got.ShouldHaveSingleItem();
        tuple.Subject.Id.ShouldBe("*");
        tuple.Subject.IsWildcard.ShouldBeTrue();
    }

    [Fact]
    public async Task Order_by_object_id_is_byte_ordinal()
    {
        var t = new TenantContext("coll-order", "tOrder");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerRelationStore(fx.ConnectionString);

        string[] ids = ["Zebra", "apple", "Banana", "Apple"];

        await using (var u = await Factory.BeginAsync())
        {
            await store.WriteAsync(t,
                [.. ids.Select(id => new RelationTuple(new EntityRef("resource", id), "viewer", new SubjectRef("user", "u1")))],
                [], u);
            await u.CommitAsync();
        }

        await using var conn = await fx.OpenAsync();
        var ordered = (await conn.QueryAsync<string>(
            "SELECT object_id FROM custodex.relation_tuples WHERE store_id = @s AND tenant_id = @t AND object_type = 'resource' ORDER BY object_id",
            new { s = t.Store, t = t.Tenant })).ToList();

        var expected = ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
        ordered.ShouldBe(expected);
    }
}
