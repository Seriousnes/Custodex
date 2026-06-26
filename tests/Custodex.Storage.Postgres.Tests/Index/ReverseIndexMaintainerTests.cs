using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.Postgres.Index;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ReverseIndexMaintainerTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlIndexStore _index = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Build();

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        _index = new NpgsqlIndexStore(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private async Task<TenantContext> SeedAndBuildAsync(string store, params RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
                new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
                new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, Build(), u);
            if (tuples.Length > 0) await _relations.WriteAsync(t, tuples, [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }
        return t;
    }

    private ReverseIndexMaintainer Maintainer() =>
        new(_schemas, _relations, _attributes, _index);

    private async Task<string[]> EditableDocsAsync(TenantContext t, string user) =>
        (await _index.QueryObjectsAsync(t, "v1", $"user:{user}", "edit", "doc", 1000, null))
        .Select(r => r.ObjectId).OrderBy(x => x).ToArray();

    private async Task WriteAndMaintainAsync(TenantContext t, RelationTuple[] add, RelationTuple[] remove)
    {
        await using var u = await _factory.BeginAsync();
        await _relations.WriteAsync(t, add, remove, u);
        await Maintainer().MaintainAsync(t, [.. add, .. remove], u);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Adding_a_blocked_tuple_removes_the_index_row()
    {
        var t = await SeedAndBuildAsync("mt-block-add",
            Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")));
        (await EditableDocsAsync(t, "alice")).ShouldBe(["alpha"]);

        await WriteAndMaintainAsync(t, [Tup("doc", "alpha", "blocked", new SubjectRef("user", "alice"))], []);
        (await EditableDocsAsync(t, "alice")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_a_blocked_tuple_re_adds_the_index_row()
    {
        var t = await SeedAndBuildAsync("mt-block-remove",
            Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")),
            Tup("doc", "alpha", "blocked", new SubjectRef("user", "alice")));
        (await EditableDocsAsync(t, "alice")).ShouldBeEmpty();

        await WriteAndMaintainAsync(t, [], [Tup("doc", "alpha", "blocked", new SubjectRef("user", "alice"))]);
        (await EditableDocsAsync(t, "alice")).ShouldBe(["alpha"]);
    }

    [Fact]
    public async Task Block_on_a_multipath_object_removes_the_row_even_with_a_second_grant_path()
    {
        var t = await SeedAndBuildAsync("mt-multipath",
            Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")),
            Tup("doc", "alpha", "editor", new SubjectRef("group", "team", "member")),
            Tup("group", "team", "member", new SubjectRef("user", "alice")));
        (await EditableDocsAsync(t, "alice")).ShouldBe(["alpha"]);

        await WriteAndMaintainAsync(t, [Tup("doc", "alpha", "blocked", new SubjectRef("user", "alice"))], []);
        (await EditableDocsAsync(t, "alice")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_group_membership_change_ripples_to_objects_granting_to_the_group()
    {
        var t = await SeedAndBuildAsync("mt-group",
            Tup("doc", "alpha", "editor", new SubjectRef("group", "team", "member")),
            Tup("group", "team", "member", new SubjectRef("user", "alice")));
        (await EditableDocsAsync(t, "carol")).ShouldBeEmpty();

        await WriteAndMaintainAsync(t, [Tup("group", "team", "member", new SubjectRef("user", "carol"))], []);
        (await EditableDocsAsync(t, "carol")).ShouldBe(["alpha"]);
    }

    [Fact]
    public async Task An_arrow_reachable_change_ripples_through_the_structural_edge()
    {
        var schema = new SchemaBuilder("v1")
            .Type("area", x => x.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor")))
            .Type("item", x => x.Relation("area", s => s.Type("area")).Permission("edit", p => p.Arrow("area", "edit")))
            .Build();
        var t = new TenantContext("mt-arrow", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, schema, u);
            await _relations.WriteAsync(t, [Tup("item", "i1", "area", new SubjectRef("area", "a1"))], [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            var add = new[] { Tup("area", "a1", "editor", new SubjectRef("user", "dana")) };
            await _relations.WriteAsync(t, add, [], u);
            await Maintainer().MaintainAsync(t, add, u);
            await u.CommitAsync();
        }

        var dana = (await _index.QueryObjectsAsync(t, "v1", "user:dana", "edit", "item", 1000, null)).Select(r => r.ObjectId);
        dana.ShouldBe(["i1"]);
    }

    [Fact]
    public async Task Maintenance_is_a_noop_when_the_index_is_not_built_for_the_active_version()
    {
        var t = new TenantContext("mt-unbuilt", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, Build(), u);
            await u.CommitAsync();
        }

        await WriteAndMaintainAsync(t, [Tup("doc", "alpha", "editor", new SubjectRef("user", "alice"))], []);
        (await _index.IsBuiltAsync(t, "v1")).ShouldBeFalse();
        (await EditableDocsAsync(t, "alice")).ShouldBeEmpty();
    }
}
