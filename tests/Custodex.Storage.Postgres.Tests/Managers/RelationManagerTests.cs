using Custodex.Abstractions;
using Custodex.Storage.Postgres.Managers;

using Dapper;

using Shouldly;

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

    [Fact]
    public async Task WriteAttributes_persists_attrs_audits_actor_epoch_and_captures_before_after_on_repeat_write()
    {
        var (t, manager, cache) = await BuildAsync("mgr-attrs");
        var attrStore = new NpgsqlAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("asset", "r1");

        Dictionary<string, object?> firstAttrs = new() { ["is_flagged"] = true, ["weight"] = 12.5 };
        await manager.WriteAttributesAsync(t, "admin-a", obj, firstAttrs);

        var stored = await attrStore.GetAsync(t, obj);
        stored.ShouldNotBeNull();
        stored!["is_flagged"]!.ToString().ShouldBe("True");

        var log1 = await _changeLog.ReadAsync(t, new ChangeLogFilter());
        log1.ShouldHaveSingleItem();
        log1[0].Actor.ShouldBe("admin-a");
        log1[0].Operation.ShouldBe("write");

        (await cache.GetEpochAsync(t)).ShouldBe(1);

        Dictionary<string, object?> secondAttrs = new() { ["is_flagged"] = false, ["weight"] = 9.0 };
        await manager.WriteAttributesAsync(t, "admin-a", obj, secondAttrs);

        var storedAfter = await attrStore.GetAsync(t, obj);
        storedAfter.ShouldNotBeNull();
        storedAfter!["is_flagged"]!.ToString().ShouldBe("False");

        var log2 = await _changeLog.ReadAsync(t, new ChangeLogFilter());
        log2.Count.ShouldBe(2);
        log2[1].Actor.ShouldBe("admin-a");
        log2[1].Operation.ShouldBe("write");

        (await cache.GetEpochAsync(t)).ShouldBe(2);
    }
}
