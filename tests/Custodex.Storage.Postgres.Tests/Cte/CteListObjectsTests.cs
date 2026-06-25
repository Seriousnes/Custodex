using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteListObjectsTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("asset", t => t
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

    private static ListObjectsRequest Req(TenantContext t, string user, int pageSize = 100, string? token = null) => new(
        t, new SubjectRef("user", user), "asset", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Lists_only_confirmed_objects_respecting_exclusion()
    {
        var (auth, t) = await SetupAsync("lo-excl",
            Tup("asset", "ka", "editor", new SubjectRef("group", "herd", "member")),
            Tup("asset", "wa", "editor", new SubjectRef("group", "herd", "member")),
            Tup("asset", "wa", "blocked", new SubjectRef("user", "alice")),
            Tup("group", "herd", "member", new SubjectRef("user", "alice")));

        var result = await auth.ListObjectsAsync(Req(t, "alice"));
        result.ObjectIds.ShouldBe(["ka"]);
        result.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_lists_every_object_of_the_type()
    {
        var (auth, t) = await SetupAsync("lo-wild",
            Tup("asset", "ka", "editor", new SubjectRef("user", "*")),
            Tup("asset", "wa", "editor", new SubjectRef("user", "*")),
            Tup("asset", "em", "editor", new SubjectRef("user", "*")));

        var result = await auth.ListObjectsAsync(Req(t, "anyone"));
        result.ObjectIds.ShouldBe(["em", "ka", "wa"]);
    }

    [Fact]
    public async Task Lists_objects_in_ordinal_order_regardless_of_server_collation()
    {
        var (auth, t) = await SetupAsync("lo-collation",
            Tup("asset", "alpha", "editor", new SubjectRef("user", "*")),
            Tup("asset", "Bravo", "editor", new SubjectRef("user", "*")),
            Tup("asset", "Zulu", "editor", new SubjectRef("user", "*")));

        var result = await auth.ListObjectsAsync(Req(t, "anyone"));
        result.ObjectIds.ShouldBe(["Bravo", "Zulu", "alpha"]);
    }

    [Fact]
    public async Task Paginates_to_exact_page_size_with_resumable_cursor()
    {
        var (auth, t) = await SetupAsync("lo-page",
            Tup("asset", "a", "editor", new SubjectRef("user", "*")),
            Tup("asset", "b", "editor", new SubjectRef("user", "*")),
            Tup("asset", "c", "editor", new SubjectRef("user", "*")),
            Tup("asset", "d", "editor", new SubjectRef("user", "*")),
            Tup("asset", "e", "editor", new SubjectRef("user", "*")));

        var p1 = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2));
        p1.ObjectIds.ShouldBe(["a", "b"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2, token: p1.ContinuationToken));
        p2.ObjectIds.ShouldBe(["c", "d"]);

        var p3 = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2, token: p2.ContinuationToken));
        p3.ObjectIds.ShouldBe(["e"]);
        p3.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Pages_do_not_overlap_or_drop_across_the_full_range()
    {
        var (auth, t) = await SetupAsync("lo-range",
            Tup("asset", "a", "editor", new SubjectRef("user", "*")),
            Tup("asset", "b", "editor", new SubjectRef("user", "*")),
            Tup("asset", "c", "editor", new SubjectRef("user", "*")));

        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2, token: token));
            all.AddRange(page.ObjectIds);
            token = page.ContinuationToken;
        } while (token is not null);

        all.ShouldBe(["a", "b", "c"]);
    }
}
