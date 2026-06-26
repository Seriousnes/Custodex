using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class AuditedWritePathTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlChangeLogStore _changeLog = null!;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _changeLog = new NpgsqlChangeLogStore(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(TenantContext t, PostgresCacheStore cache)> SeedTenantAsync(string tenantId)
    {
        var t = new TenantContext("store-a", tenantId);
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
        var cache = new PostgresCacheStore(fx.ConnectionString, t);
        return (t, cache);
    }

    private static RelationTuple Tuple => new(
        new EntityRef("doc", "d1"), "writer", new SubjectRef("group", "team", "member"));

    [Fact]
    public async Task Commit_persists_tuple_change_log_row_and_epoch_bump_together()
    {
        var (t, cache) = await SeedTenantAsync("audit-commit");
        var path = new AuditedWritePath(_relations,
            new NpgsqlAttributeStore(fx.ConnectionString),
            new NpgsqlSchemaStore(fx.ConnectionString),
            _changeLog, cache);

        await using (var u = await _factory.BeginAsync())
        {
            await path.WriteTuplesAsync(t, "dr-admin", [Tuple], [], u);
            await u.CommitAsync();
        }

        (await _relations.GetByObjectAsync(t, new EntityRef("doc", "d1"), "writer"))
            .ShouldHaveSingleItem();

        var log = await _changeLog.ReadAsync(t, new ChangeLogFilter());
        var e = log.ShouldHaveSingleItem();
        e.Actor.ShouldBe("dr-admin");
        e.Operation.ShouldBe("write");
        e.Target.ShouldBe("doc:d1#writer@group:team#member");

        (await cache.GetEpochAsync(t)).ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_discards_tuple_change_log_row_and_epoch_bump_together()
    {
        var (t, cache) = await SeedTenantAsync("audit-rollback");
        var path = new AuditedWritePath(_relations,
            new NpgsqlAttributeStore(fx.ConnectionString),
            new NpgsqlSchemaStore(fx.ConnectionString),
            _changeLog, cache);

        await using (var u = await _factory.BeginAsync())
        {
            await path.WriteTuplesAsync(t, "dr-admin", [Tuple], [], u);
        }

        (await _relations.GetByObjectAsync(t, new EntityRef("doc", "d1"), "writer"))
            .ShouldBeEmpty();
        (await _changeLog.ReadAsync(t, new ChangeLogFilter())).ShouldBeEmpty();
        (await cache.GetEpochAsync(t)).ShouldBe(0);
    }
}
