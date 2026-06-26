using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class RelationStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Write_then_get_by_object_round_trips_a_conditioned_tuple()
    {
        var t = new TenantContext("s1", "tA");
        await SeedTenantAsync(t);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        var tuple = new RelationTuple(
            new EntityRef("folder", "item-1"), "writer",
            new SubjectRef("group", "reds", "member"),
            new ConditionRef("within_hours",
                new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }

        var found = await store.GetByObjectAsync(t, new EntityRef("folder", "item-1"), "writer");
        var got = found.ShouldHaveSingleItem();
        got.Subject.ShouldBe(new SubjectRef("group", "reds", "member"));
        got.Condition.ShouldNotBeNull();
        got.Condition!.Name.ShouldBe("within_hours");
        got.Condition.Parameters["start"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Write_remove_deletes_by_natural_key()
    {
        var t = new TenantContext("s1", "tDel");
        await SeedTenantAsync(t);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        var tuple = new RelationTuple(
            new EntityRef("resource", "r1"), "editor", new SubjectRef("user", "alice"));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [], [tuple], u);
            await u.CommitAsync();
        }

        (await store.GetByObjectAsync(t, new EntityRef("resource", "r1"), "editor")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_by_subject_returns_tuples_for_that_subject()
    {
        var t = new TenantContext("s1", "tSub");
        await SeedTenantAsync(t);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        var tuple = new RelationTuple(
            new EntityRef("resource", "r9"), "editor", new SubjectRef("user", "carol"));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }

        var bySubject = await store.GetBySubjectAsync(t, new SubjectRef("user", "carol"));
        bySubject.ShouldHaveSingleItem().Object.ShouldBe(new EntityRef("resource", "r9"));
    }

    [Fact]
    public async Task WriteAsync_upsert_updates_condition_on_repeated_natural_key()
    {
        var t = new TenantContext("s1", "tUpsert");
        await SeedTenantAsync(t);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        var subject = new SubjectRef("group", "reds", "member");
        var first = new RelationTuple(new EntityRef("resource", "r1"), "editor", subject);

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [first], [], u);
            await u.CommitAsync();
        }

        var second = new RelationTuple(
            new EntityRef("resource", "r1"), "editor", subject,
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 9 }));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [second], [], u);
            await u.CommitAsync();
        }

        var found = await store.GetByObjectAsync(t, new EntityRef("resource", "r1"), "editor");
        var got = found.ShouldHaveSingleItem();
        got.Subject.ShouldBe(subject);
        got.Condition.ShouldNotBeNull();
        got.Condition!.Name.ShouldBe("within_hours");
        got.Condition.Parameters["start"]!.ToString().ShouldBe("9");
    }

    [Fact]
    public async Task ListObjectIdsAsync_returns_distinct_ids_for_type_tenant_isolated()
    {
        var tA = new TenantContext("s1", "tListA");
        var tB = new TenantContext("s1", "tListB");
        await SeedTenantAsync(tA);
        await SeedTenantAsync(tB);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(tA,
            [
                new RelationTuple(new EntityRef("resource", "r1"), "editor", new SubjectRef("user", "alice")),
                new RelationTuple(new EntityRef("resource", "r1"), "reader", new SubjectRef("user", "alice")),
                new RelationTuple(new EntityRef("resource", "r2"), "editor", new SubjectRef("user", "alice")),
                new RelationTuple(new EntityRef("folder", "f1"), "editor", new SubjectRef("user", "alice")),
            ], [], u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(tB,
            [
                new RelationTuple(new EntityRef("resource", "r9"), "editor", new SubjectRef("user", "alice")),
            ], [], u);
            await u.CommitAsync();
        }

        var ids = await store.ListObjectIdsAsync(tA, "resource");
        ids.ShouldBe(["r1", "r2"], ignoreOrder: true);
        ids.ShouldNotContain("f1");
        ids.ShouldNotContain("r9");
    }
}
