using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class UowBoundRelationReadTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Bound_read_sees_uncommitted_write_unbound_does_not()
    {
        var t = new TenantContext("uowbound", "t");

        await using (var seed = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(seed);
            await uow.Connection.ExecuteAsync(
                "INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
                new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync(
                "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
                new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await seed.CommitAsync();
        }

        var store = new NpgsqlRelationStore(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var tuple = new RelationTuple(new EntityRef("doc", "r1"), "editor", new SubjectRef("user", "carol"));
        await store.WriteAsync(t, [tuple], [], u);

        (await store.OnUnitOfWork(u).GetByObjectAsync(t, new EntityRef("doc", "r1"), "editor")).ShouldHaveSingleItem();
        (await store.GetByObjectAsync(t, new EntityRef("doc", "r1"), "editor")).ShouldBeEmpty();

        await u.CommitAsync();
    }
}
