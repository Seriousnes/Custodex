using Custodex.Abstractions;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class AuditedWritePathTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private AuditedWritePath _path = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlChangeLogStore _changeLog = null!;
    private PostgresCacheStore _cache = null!;
    private readonly TenantContext _t = new("store-a", "audit");

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _changeLog = new NpgsqlChangeLogStore(fx.ConnectionString);
        _cache = new PostgresCacheStore(fx.ConnectionString, _t);
        _path = new AuditedWritePath(_relations, attributes, schemas, _changeLog, _cache);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static RelationTuple Tuple => new(
        new EntityRef("doc", "d1"), "writer", new SubjectRef("group", "team", "member"));

    [Fact]
    public async Task Commit_persists_tuple_change_log_row_and_epoch_bump_together()
    {
        await using (var u = await _factory.BeginAsync())
        {
            await _path.WriteTuplesAsync(_t, "dr-admin", [Tuple], [], u);
            await u.CommitAsync();
        }

        (await _relations.GetByObjectAsync(_t, new EntityRef("doc", "d1"), "writer"))
            .ShouldHaveSingleItem();

        var log = await _changeLog.ReadAsync(_t, new ChangeLogFilter());
        var e = log.ShouldHaveSingleItem();
        e.Actor.ShouldBe("dr-admin");
        e.Operation.ShouldBe("write");
        e.Target.ShouldBe("doc:d1#writer@group:team#member");

        (await _cache.GetEpochAsync(_t)).ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_discards_tuple_change_log_row_and_epoch_bump_together()
    {
        await using (var u = await _factory.BeginAsync())
        {
            await _path.WriteTuplesAsync(_t, "dr-admin", [Tuple], [], u);
        }

        (await _relations.GetByObjectAsync(_t, new EntityRef("doc", "d1"), "writer"))
            .ShouldBeEmpty();
        (await _changeLog.ReadAsync(_t, new ChangeLogFilter())).ShouldBeEmpty();
        (await _cache.GetEpochAsync(_t)).ShouldBe(0);
    }
}
