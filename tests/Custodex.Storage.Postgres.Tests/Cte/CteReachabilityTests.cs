using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteReachabilityTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("cte", "t");

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
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await _relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "staff", "member")),
            new RelationTuple(new EntityRef("group", "staff"), "member", new SubjectRef("group", "crew", "member")),
            new RelationTuple(new EntityRef("group", "crew"), "member", new SubjectRef("user", "pat")),
            new RelationTuple(new EntityRef("group", "crew"), "member", new SubjectRef("user", "jones")),
            new RelationTuple(new EntityRef("asset", "A1"), "folder", new SubjectRef("folder", "KH1")),
        ], [], u);
        await u.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Subjects_through_relation_expands_nested_groups_to_leaf_users()
    {
        await using var conn = await fx.OpenAsync();
        var leaves = await CteReachability.SubjectsThroughRelationAsync(
            conn, null, T, new EntityRef("doc", "D1"), "viewer");

        leaves.Select(s => s.Id).OrderBy(x => x).ShouldBe(["jones", "pat"]);
        leaves.ShouldAllBe(s => s.Relation == null);
    }

    [Fact]
    public async Task Edges_through_relation_returns_direct_structural_tuples_unexpanded()
    {
        await using var conn = await fx.OpenAsync();
        var edges = await CteReachability.EdgesThroughRelationAsync(
            conn, null, T, new EntityRef("asset", "A1"), "folder");

        var edge = edges.ShouldHaveSingleItem();
        edge.Subject.ShouldBe(new SubjectRef("folder", "KH1"));
    }
}
