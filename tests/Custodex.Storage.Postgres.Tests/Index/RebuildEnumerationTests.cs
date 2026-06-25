using Dapper;
using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class RebuildEnumerationTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("rebuild-enum", "t");

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
            new RelationTuple(new EntityRef("doc", "alpha"), "editor", new SubjectRef("group", "team", "member")),
            new RelationTuple(new EntityRef("doc", "beta"), "editor", new SubjectRef("user", "*")),
            new RelationTuple(new EntityRef("group", "team"), "member", new SubjectRef("user", "alice")),
            new RelationTuple(new EntityRef("group", "team"), "member", new SubjectRef("user", "bob")),
        ], [], u);
        await u.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Load_projects_concrete_users_and_objects_by_type()
    {
        await using var conn = await fx.OpenAsync();
        var inputs = await RebuildEnumeration.LoadAsync(conn, T);

        inputs.Users.OrderBy(x => x).ShouldBe(["alice", "bob"]);
        inputs.ObjectIdsByType["doc"].OrderBy(x => x).ShouldBe(["alpha", "beta"]);
        inputs.ObjectIdsByType["group"].ShouldBe(["team"]);
    }
}
