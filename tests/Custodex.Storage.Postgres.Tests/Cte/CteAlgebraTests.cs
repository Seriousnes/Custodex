using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteAlgebraTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private TenantContext _t = default;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<NpgsqlCteAuthorizer> SetupAsync(string store, Schema schema, params RelationTuple[] tuples)
    {
        _t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(_t.Store, schema, u);
        if (tuples.Length > 0) await _relations.WriteAsync(_t, tuples, [], u);
        await u.CommitAsync();
        return new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
    }

    private CheckRequest Req(EntityRef obj, string perm, string subjectId) => new(
        _t, obj, perm, new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId), new Dictionary<string, object?>()));

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Union_grants_if_either_branch_holds()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Relation("editor", s => s.User())
            .Permission("access", p => p.Relation("viewer").Union(x => x.Relation("editor")))).Build();
        var auth = await SetupAsync("alg-union", schema, Tup("doc", "D1", "editor", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Intersect_requires_both_branches()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("reader", s => s.User()).Relation("trained", s => s.User())
            .Permission("access", p => p.Relation("reader").Intersect(x => x.Relation("trained")))).Build();
        var auth = await SetupAsync("alg-inter", schema,
            Tup("doc", "D1", "reader", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "trained", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "reader", new SubjectRef("user", "bob")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_revokes_the_right_branch()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Relation("blocked", s => s.User())
            .Permission("access", p => p.Relation("viewer").Exclude(x => x.Relation("blocked")))).Build();
        var auth = await SetupAsync("alg-excl", schema,
            Tup("doc", "D1", "viewer", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "carol")),
            Tup("doc", "D1", "blocked", new SubjectRef("user", "carol")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "carol"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_sees_inner_exclusion_on_the_related_object()
    {
        var schema = new SchemaBuilder("v1")
            .Type("folder", t => t
                .Relation("editor", s => s.User()).Relation("blocked", s => s.User())
                .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
            .Type("asset", t => t
                .Relation("folder", s => s.Type("folder"))
                .Permission("edit", p => p.Arrow("folder", "edit")))
            .Build();
        var auth = await SetupAsync("alg-arrow-excl", schema,
            Tup("asset", "A1", "folder", new SubjectRef("folder", "KH1")),
            Tup("folder", "KH1", "editor", new SubjectRef("user", "carol")),
            Tup("folder", "KH1", "blocked", new SubjectRef("user", "carol")),
            Tup("folder", "KH1", "editor", new SubjectRef("user", "dana")));
        (await auth.CheckAsync(Req(new EntityRef("asset", "A1"), "edit", "carol"))).Allowed.ShouldBeFalse();
        (await auth.CheckAsync(Req(new EntityRef("asset", "A1"), "edit", "dana"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Arrow_falls_back_to_a_relation_when_target_is_not_a_permission()
    {
        var schema = new SchemaBuilder("v1")
            .Type("folder", t => t
                .Relation("is_locked", s => s.Wildcard("user"))
                .Permission("is_locked", p => p.Relation("is_locked")))
            .Type("asset", t => t
                .Relation("folder", s => s.Type("folder"))
                .Permission("locked", p => p.Arrow("folder", "is_locked")))
            .Build();
        var auth = await SetupAsync("alg-arrow-rel", schema,
            Tup("asset", "A1", "folder", new SubjectRef("folder", "Q1")),
            Tup("folder", "Q1", "is_locked", new SubjectRef("user", "*")));
        (await auth.CheckAsync(Req(new EntityRef("asset", "A1"), "locked", "anyone"))).Allowed.ShouldBeTrue();
    }
}
