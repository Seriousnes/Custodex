using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.Postgres.Index;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedWritePathTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlChangeLogStore _changeLog = null!;
    private PostgresCacheStore _cache = null!;
    private NpgsqlIndexStore _index = null!;

    private static Schema BuildV1() => new SchemaBuilder("v1")
        .Type("doc", t => t.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor"))).Build();
    private static Schema BuildV2() => new SchemaBuilder("v2")
        .Type("doc", t => t.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor"))).Build();

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _changeLog = new NpgsqlChangeLogStore(fx.ConnectionString);
        _index = new NpgsqlIndexStore(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private IndexedWritePath NewPath(TenantContext t)
    {
        _cache = new PostgresCacheStore(fx.ConnectionString, t);
        var audited = new AuditedWritePath(_relations, _attributes, _schemas, _changeLog, _cache);
        var maintainer = new ReverseIndexMaintainer(_schemas, _relations, _attributes, _index);
        return new IndexedWritePath(audited, maintainer, _index);
    }

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Write_through_the_path_maintains_the_index_atomically()
    {
        var t = new TenantContext("iwp-write", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, BuildV1(), u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await NewPath(t).WriteTuplesAsync(t, "admin", [Tup("doc", "alpha", "editor", new SubjectRef("user", "alice"))], [], u);
            await u.CommitAsync();
        }

        var rows = await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "doc", 100, null);
        rows.Select(r => r.ObjectId).ShouldBe(["alpha"]);
    }

    [Fact]
    public async Task A_rolled_back_write_leaves_the_index_unchanged()
    {
        var t = new TenantContext("iwp-rollback", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, BuildV1(), u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await NewPath(t).WriteTuplesAsync(t, "admin", [Tup("doc", "beta", "editor", new SubjectRef("user", "alice"))], [], u);
        }

        (await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "doc", 100, null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Setting_a_new_schema_clears_the_index_for_the_old_version()
    {
        var t = new TenantContext("iwp-schema", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, BuildV1(), u);
            await _relations.WriteAsync(t, [Tup("doc", "alpha", "editor", new SubjectRef("user", "alice"))], [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }
        (await _index.IsBuiltAsync(t, "v1")).ShouldBeTrue();

        await using (var u = await _factory.BeginAsync())
        {
            await NewPath(t).SetSchemaAsync(t.Store, t, "admin", BuildV2(), u);
            await u.CommitAsync();
        }

        (await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "doc", 100, null)).ShouldBeEmpty();
        (await _index.IsBuiltAsync(t, "v2")).ShouldBeFalse();
    }
}
