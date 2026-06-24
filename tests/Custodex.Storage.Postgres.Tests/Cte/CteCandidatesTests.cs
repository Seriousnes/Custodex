using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteCandidatesTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("cte", "candidates");

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
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await _relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("asset", "ka"), "editor", new SubjectRef("group", "herd", "member")),
            new RelationTuple(new EntityRef("asset", "wa"), "editor", new SubjectRef("group", "herd", "member")),
            new RelationTuple(new EntityRef("asset", "em"), "editor", new SubjectRef("user", "someone-else")),
            new RelationTuple(new EntityRef("group", "herd"), "member", new SubjectRef("user", "alice")),
        ], [], u);
        await u.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reachable_gathers_objects_via_direct_and_nested_group_grants_sorted()
    {
        await using var conn = await fx.OpenAsync();
        var ids = await CteCandidates.ReachableObjectIdsAsync(conn, T, new SubjectRef("user", "alice"), "asset");
        ids.ShouldBe(["ka", "wa"]);
    }

    [Fact]
    public async Task Type_universe_returns_every_object_of_the_type()
    {
        await using var conn = await fx.OpenAsync();
        var ids = await CteCandidates.TypeUniverseAsync(conn, T, "asset");
        ids.ShouldBe(["em", "ka", "wa"]);
    }
}
