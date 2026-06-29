using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.SqlServer.Index;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Index;

[Collection("sqlserver")]
public class IndexedWritePathTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);
    private readonly SqlServerRelationStore _relations = new(fx.ConnectionString);
    private readonly SqlServerAttributeStore _attributes = new(fx.ConnectionString);
    private readonly SqlServerSchemaStore _schemas = new(fx.ConnectionString);
    private readonly SqlServerChangeLogStore _changeLog = new(fx.ConnectionString);
    private readonly SqlServerIndexStore _index = new(fx.ConnectionString);

    private static Schema BuildV1() => new SchemaBuilder("v1")
        .Type("doc", t => t.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor"))).Build();
    private static Schema BuildV2() => new SchemaBuilder("v2")
        .Type("doc", t => t.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor"))).Build();

    private IndexedWritePath NewPath(TenantContext t)
    {
        var cache = new SqlServerCacheStore(fx.ConnectionString, t);
        var audited = new AuditedWritePath(_relations, _attributes, _schemas, _changeLog, cache);
        var maintainer = new ReverseIndexMaintainer(_schemas, _relations, _attributes, _index);
        return new IndexedWritePath(audited, maintainer, _index);
    }

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private async Task SetSchemaAsync(TenantContext t, Schema schema, params RelationTuple[] tuples)
    {
        await Seed.TenantAsync(Factory, t);
        await using var u = await Factory.BeginAsync();
        await _schemas.SetActiveAsync(t.Store, schema, u);
        if (tuples.Length > 0)
            await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
    }

    private async Task RebuildAsync(TenantContext t)
    {
        await using var u = await Factory.BeginAsync();
        await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Write_through_the_path_maintains_the_index_atomically()
    {
        var t = new TenantContext("iwp-write", "t");
        await SetSchemaAsync(t, BuildV1());
        await RebuildAsync(t);

        await using (var u = await Factory.BeginAsync())
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
        await SetSchemaAsync(t, BuildV1());
        await RebuildAsync(t);

        await using (var u = await Factory.BeginAsync())
        {
            await NewPath(t).WriteTuplesAsync(t, "admin", [Tup("doc", "beta", "editor", new SubjectRef("user", "alice"))], [], u);
        }

        (await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "doc", 100, null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Setting_a_new_schema_clears_the_index_for_the_old_version()
    {
        var t = new TenantContext("iwp-schema", "t");
        await SetSchemaAsync(t, BuildV1(), Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")));
        await RebuildAsync(t);
        (await _index.IsBuiltAsync(t, "v1")).ShouldBeTrue();

        await using (var u = await Factory.BeginAsync())
        {
            await NewPath(t).SetSchemaAsync(t.Store, t, "admin", BuildV2(), u);
            await u.CommitAsync();
        }

        (await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "doc", 100, null)).ShouldBeEmpty();
        (await _index.IsBuiltAsync(t, "v2")).ShouldBeFalse();
    }
}
