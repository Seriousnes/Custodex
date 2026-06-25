using Dapper;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ReverseIndexRebuildTests(PostgresFixture fx) : IAsyncLifetime
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
        .Condition("within_hours", c => c.Int("start").Int("end"))
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

    private async Task<TenantContext> SeedAsync(string store, params RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(t.Store, Build(), u);
        if (tuples.Length > 0) await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
        return t;
    }

    private ReverseIndexRebuilder NewRebuilder() =>
        new(fx.ConnectionString, _schemas, _relations, _attributes, _index);

    private async Task RebuildAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        await NewRebuilder().RebuildAsync(t, u);
        await u.CommitAsync();
    }

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Rebuild_stores_structural_grants_and_marks_built()
    {
        var t = await SeedAsync("rb-basic",
            Tup("doc", "alpha", "editor", new SubjectRef("group", "team", "member")),
            Tup("doc", "beta", "editor", new SubjectRef("group", "team", "member")),
            Tup("doc", "beta", "blocked", new SubjectRef("user", "alice")),
            Tup("group", "team", "member", new SubjectRef("user", "alice")));
        await RebuildAsync(t);

        (await _index.IsBuiltAsync(t, "v1")).ShouldBeTrue();
        var alice = await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "doc", 100, null);
        alice.Select(r => r.ObjectId).ShouldBe(["alpha"]);
        alice.ShouldAllBe(r => r.Conditioned == false);
    }

    [Fact]
    public async Task Rebuild_stores_a_wildcard_grant_under_the_star_subject()
    {
        var t = await SeedAsync("rb-wild",
            Tup("doc", "gamma", "editor", new SubjectRef("user", "*")));
        await RebuildAsync(t);

        var star = await _index.QueryObjectsAsync(t, "v1", "user:*", "edit", "doc", 100, null);
        star.Select(r => r.ObjectId).ShouldBe(["gamma"]);
    }

    [Fact]
    public async Task Rebuild_flags_conditioned_grants()
    {
        var t = await SeedAsync("rb-cond",
            Tup("doc", "alpha", "editor", new SubjectRef("user", "carol")) with
            {
                Condition = new ConditionRef("within_hours",
                    new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 })
            });
        await RebuildAsync(t);

        var rows = await _index.QueryObjectsAsync(t, "v1", "user:carol", "edit", "doc", 100, null);
        rows.ShouldHaveSingleItem();
        rows[0].ObjectId.ShouldBe("alpha");
        rows[0].Conditioned.ShouldBeTrue();
    }

    [Fact]
    public async Task Rebuild_is_idempotent_and_replaces_prior_rows()
    {
        var t = await SeedAsync("rb-idem",
            Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")));
        await RebuildAsync(t);
        await RebuildAsync(t);

        var rows = await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "doc", 100, null);
        rows.ShouldHaveSingleItem().ObjectId.ShouldBe("alpha");
    }
}
