using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteListSubjectsTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
        .Build();

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

    private async Task<(NpgsqlCteAuthorizer Auth, TenantContext T)> SetupAsync(string store, params RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(t.Store, Build(), u);
        await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
        return (new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator()), t);
    }

    private static ListSubjectsRequest Req(TenantContext t, int pageSize = 100, string? token = null) => new(
        t, new EntityRef("doc", "D1"), "view",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Lists_leaf_users_via_nested_groups_honouring_exclusion()
    {
        var (auth, t) = await SetupAsync("ls-excl",
            Tup("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tup("group", "staff", "member", new SubjectRef("user", "alice")),
            Tup("group", "staff", "member", new SubjectRef("user", "bob")),
            Tup("doc", "D1", "blocked", new SubjectRef("user", "bob")));

        var result = await auth.ListSubjectsAsync(Req(t));
        result.Subjects.Select(s => s.Id).ShouldBe(["alice"]);
    }

    [Fact]
    public async Task Paginates_subjects_to_exact_page_size()
    {
        var (auth, t) = await SetupAsync("ls-page",
            Tup("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tup("group", "staff", "member", new SubjectRef("user", "a")),
            Tup("group", "staff", "member", new SubjectRef("user", "b")),
            Tup("group", "staff", "member", new SubjectRef("user", "c")));

        var p1 = await auth.ListSubjectsAsync(Req(t, pageSize: 2));
        p1.Subjects.Select(s => s.Id).ShouldBe(["a", "b"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListSubjectsAsync(Req(t, pageSize: 2, token: p1.ContinuationToken));
        p2.Subjects.Select(s => s.Id).ShouldBe(["c"]);
        p2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_surfaces_as_star_and_respects_page_size()
    {
        var (auth, t) = await SetupAsync("ls-wild",
            Tup("doc", "D1", "viewer", new SubjectRef("user", "*")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "a")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "b")));

        var p1 = await auth.ListSubjectsAsync(Req(t, pageSize: 2));
        p1.Subjects.Count.ShouldBe(2);
        p1.Subjects.Select(s => s.Id).ShouldBe(["*", "a"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListSubjectsAsync(Req(t, pageSize: 2, token: p1.ContinuationToken));
        p2.Subjects.Select(s => s.Id).ShouldBe(["b"]);
        p2.ContinuationToken.ShouldBeNull();
    }
}
