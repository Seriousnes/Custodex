using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteWorkedExampleParityTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

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

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private async Task<(NpgsqlCteAuthorizer Auth, TenantContext T)> SetupAsync(string store, Schema schema, RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(t.Store, schema, u);
        await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
        return (new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator()), t);
    }

    private static CheckRequest Req(TenantContext t, EntityRef obj, string perm, string sid) => new(
        t, obj, perm, new SubjectRef("user", sid),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", sid), new Dictionary<string, object?>()));

    [Fact]
    public async Task Role_grant_over_category_resolves_through_the_arrow()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("category", t => t
                .Relation("granter", s => s.User().SubjectSet("group", "member"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("record_grant", p => p.Relation("granter").Exclude(x => x.Relation("blocked"))))
            .Type("inventory_item", t => t
                .Relation("granter", s => s.User().SubjectSet("group", "member"))
                .Relation("category", s => s.Type("category"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("record_grant", p => p.Relation("granter")
                    .Arrow("category", "record_grant").Exclude(x => x.Relation("blocked"))))
            .Build();
        var (auth, t) = await SetupAsync("ex-arrow-cat", schema,
        [
            Tup("category", "cat1", "granter", new SubjectRef("group", "crew", "member")),
            Tup("inventory_item", "item-X", "category", new SubjectRef("category", "cat1")),
            Tup("group", "crew", "member", new SubjectRef("user", "pat")),
        ]);
        (await auth.CheckAsync(Req(t, new EntityRef("inventory_item", "item-X"), "record_grant", "pat")))
            .Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(t, new EntityRef("inventory_item", "item-X"), "record_grant", "outsider")))
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Gate_allows_trained_member_denies_untrained()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("folder", t => t
                .Relation("is_locked", s => s.Wildcard("user"))
                .Permission("is_locked", p => p.Relation("is_locked")))
            .Type("asset", t => t
                .Relation("can_access", s => s.User().SubjectSet("group", "member"))
                .Relation("folder", s => s.Type("folder"))
                .Relation("crew_member", s => s.SubjectSet("group", "member"))
                .Relation("trained_member", s => s.SubjectSet("group", "member"))
                .Permission("access", p => p
                    .Union(b => b.Relation("can_access").Exclude(x => x.Arrow("folder", "is_locked")))
                    .Union(b => b.Arrow("folder", "is_locked")
                        .Intersect(x => x.Relation("crew_member"))
                        .Intersect(x => x.Relation("trained_member")))))
            .Build();
        var tuples = new[]
        {
            Tup("asset", "A1", "folder", new SubjectRef("folder", "Q1")),
            Tup("folder", "Q1", "is_locked", new SubjectRef("user", "*")),
            Tup("asset", "A1", "crew_member", new SubjectRef("group", "crew", "member")),
            Tup("asset", "A1", "trained_member", new SubjectRef("group", "trained", "member")),
            Tup("asset", "A1", "can_access", new SubjectRef("user", "pat")),
            Tup("asset", "A1", "can_access", new SubjectRef("user", "jones")),
            Tup("group", "crew", "member", new SubjectRef("user", "pat")),
            Tup("group", "crew", "member", new SubjectRef("user", "jones")),
            Tup("group", "trained", "member", new SubjectRef("user", "pat")),
        };
        var (auth, t) = await SetupAsync("ex-gate", schema, tuples);
        (await auth.CheckAsync(Req(t, new EntityRef("asset", "A1"), "access", "pat"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(t, new EntityRef("asset", "A1"), "access", "jones"))).Allowed.ShouldBeFalse();
    }
}
