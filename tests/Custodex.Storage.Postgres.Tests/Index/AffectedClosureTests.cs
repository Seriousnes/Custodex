using Dapper;
using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class AffectedClosureTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("affected", "t");

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = T.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await _relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("item", "i1"), "area", new SubjectRef("area", "a1")),
            new RelationTuple(new EntityRef("item", "i2"), "area", new SubjectRef("area", "a1")),
            new RelationTuple(new EntityRef("doc", "alpha"), "editor", new SubjectRef("group", "team", "member")),
            new RelationTuple(new EntityRef("group", "team"), "member", new SubjectRef("user", "alice")),
        ], [], u);
        await u.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task A_direct_grant_change_affects_just_that_object()
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        var changed = new[] { Tup("doc", "alpha", "blocked", new SubjectRef("user", "alice")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("doc", "alpha"));
        await u.CommitAsync();
    }

    [Fact]
    public async Task A_structural_edge_change_affects_objects_reaching_it_through_the_arrow()
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        var changed = new[] { Tup("area", "a1", "editor", new SubjectRef("user", "bob")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("area", "a1"));
        affected.ShouldContain(new EntityRef("item", "i1"));
        affected.ShouldContain(new EntityRef("item", "i2"));
        await u.CommitAsync();
    }

    [Fact]
    public async Task A_group_membership_change_affects_objects_granting_to_that_group()
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        var changed = new[] { Tup("group", "team", "member", new SubjectRef("user", "carol")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("group", "team"));
        affected.ShouldContain(new EntityRef("doc", "alpha"));
        await u.CommitAsync();
    }
}
