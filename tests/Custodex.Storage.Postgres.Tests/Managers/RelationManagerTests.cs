using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Managers;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Managers;

[Collection("postgres")]
public class RelationManagerTests(PostgresFixture fx) : IAsyncLifetime
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

    private async Task<(TenantContext t, CustodexRelationManager manager, PostgresCacheStore cache)>
        BuildAsync(string tenantId)
    {
        var t = new TenantContext("store-a", tenantId);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        var cache = new PostgresCacheStore(fx.ConnectionString, t);
        var auditedPath = new AuditedWritePath(_relations, attributes, schemas, _changeLog, cache);
        var manager = new CustodexRelationManager(_factory, _relations, attributes, _changeLog, auditedPath);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();

        return (t, manager, cache);
    }

    private static RelationTuple Tuple => new(
        new EntityRef("category", "cat1"), "writer", new SubjectRef("group", "team", "member"));

    [Fact]
    public async Task WriteTuples_persists_tuple_audits_actor_and_bumps_epoch_atomically()
    {
        var (t, manager, cache) = await BuildAsync("mgr-write");
        await manager.WriteTuplesAsync(t, "dr-admin", [Tuple]);

        (await _relations.GetByObjectAsync(t, new EntityRef("category", "cat1"), "writer"))
            .ShouldHaveSingleItem();

        var log = await _changeLog.ReadAsync(t, new ChangeLogFilter());
        var e = log.ShouldHaveSingleItem();
        e.Actor.ShouldBe("dr-admin");
        e.Operation.ShouldBe("write");

        (await cache.GetEpochAsync(t)).ShouldBe(1);
    }

    [Fact]
    public async Task DeleteTuples_records_a_delete_audit_with_before_image()
    {
        var (t, manager, cache) = await BuildAsync("mgr-delete");
        await manager.WriteTuplesAsync(t, "dr-admin", [Tuple]);
        await manager.DeleteTuplesAsync(t, "dr-admin", [Tuple]);

        (await _relations.GetByObjectAsync(t, new EntityRef("category", "cat1"), "writer")).ShouldBeEmpty();

        var log = await _changeLog.ReadAsync(t, new ChangeLogFilter());
        log.ShouldContain(e => e.Operation == "delete");
        (await cache.GetEpochAsync(t)).ShouldBe(2);
    }

    [Fact]
    public async Task ReadTuples_filters_by_object_type()
    {
        var (t, manager, _) = await BuildAsync("mgr-read");
        await manager.WriteTuplesAsync(t, "admin", [Tuple]);
        var read = await manager.ReadTuplesAsync(t, new TupleFilter(ObjectType: "category"));
        read.ShouldContain(x => x.Object.Id == "cat1");
    }
}
