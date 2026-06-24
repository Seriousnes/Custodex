using Dapper;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedListObjectsTests(PostgresFixture fx) : IAsyncLifetime
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

    private async Task<(IndexedAuthorizer Auth, NpgsqlCteAuthorizer Oracle, TenantContext T)> SetupAsync(
        string store, IReadOnlyList<RelationTuple> tuples, IReadOnlyList<(string Subject, string Perm, string Type, string Obj, bool Cond)> indexRows)
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
        foreach (var r in indexRows)
            await uow.Connection.ExecuteAsync("""
                INSERT INTO reverse_index (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
                VALUES (@s, @t, 'v1', @subj, @perm, @type, @obj, @cond)
                """,
                new { s = t.Store, t = t.Tenant, subj = r.Subject, perm = r.Perm, type = r.Type, obj = r.Obj, cond = r.Cond }, uow.Transaction);
        if (indexRows.Count > 0)
            await _index.MarkBuiltAsync(t, "v1", u);
        await u.CommitAsync();

        var inner = new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
        return (new IndexedAuthorizer(inner, _index, _schemas), inner, t);
    }

    private static ListObjectsRequest Req(TenantContext t, string user, int pageSize = 100, string? token = null) => new(
        t, new SubjectRef("user", user), "doc", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Returns_unconditioned_index_rows_directly()
    {
        var (auth, _, t) = await SetupAsync("ilo-uncond",
            [Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")),
             Tup("doc", "beta", "editor", new SubjectRef("user", "alice"))],
            [("user:alice", "edit", "doc", "alpha", false),
             ("user:alice", "edit", "doc", "beta", false)]);

        var result = await auth.ListObjectsAsync(Req(t, "alice"));
        result.ObjectIds.ShouldBe(["alpha", "beta"]);
        result.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Conditioned_rows_are_rechecked_and_dropped_when_the_condition_fails()
    {
        var (auth, _, t) = await SetupAsync("ilo-cond",
            [Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")),
             Tup("doc", "beta", "editor", new SubjectRef("user", "alice")),
             Tup("doc", "beta", "blocked", new SubjectRef("user", "alice"))],
            [("user:alice", "edit", "doc", "alpha", false),
             ("user:alice", "edit", "doc", "beta", true)]);

        var result = await auth.ListObjectsAsync(Req(t, "alice"));
        result.ObjectIds.ShouldBe(["alpha"]);
    }

    [Fact]
    public async Task Falls_back_to_the_cte_path_when_the_index_has_no_rows_for_the_version()
    {
        var (auth, oracle, t) = await SetupAsync("ilo-fallback",
            [Tup("doc", "gamma", "editor", new SubjectRef("user", "*"))],
            indexRows: []);

        var viaIndex = await auth.ListObjectsAsync(Req(t, "anyone"));
        var viaOracle = await oracle.ListObjectsAsync(Req(t, "anyone"));
        viaIndex.ObjectIds.ShouldBe(viaOracle.ObjectIds);
        viaIndex.ObjectIds.ShouldBe(["gamma"]);
    }

    [Fact]
    public async Task Paginates_to_exact_page_size_with_resumable_cursor()
    {
        var tuples = new List<RelationTuple>();
        var rows = new List<(string, string, string, string, bool)>();
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            tuples.Add(Tup("doc", id, "editor", new SubjectRef("user", "alice")));
            rows.Add(("user:alice", "edit", "doc", id, false));
        }
        var (auth, _, t) = await SetupAsync("ilo-page", tuples, rows);

        var p1 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2));
        p1.ObjectIds.ShouldBe(["a", "b"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2, token: p1.ContinuationToken));
        p2.ObjectIds.ShouldBe(["c", "d"]);

        var p3 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2, token: p2.ContinuationToken));
        p3.ObjectIds.ShouldBe(["e"]);
        p3.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Pagination_holds_when_conditioned_rows_drop_inside_a_page()
    {
        var tuples = new List<RelationTuple>();
        var rows = new List<(string, string, string, string, bool)>();
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            tuples.Add(Tup("doc", id, "editor", new SubjectRef("user", "alice")));
            var conditioned = id is "b" or "d";
            if (conditioned) tuples.Add(Tup("doc", id, "blocked", new SubjectRef("user", "alice")));
            rows.Add(("user:alice", "edit", "doc", id, conditioned));
        }
        var (auth, _, t) = await SetupAsync("ilo-drop", tuples, rows);

        var p1 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2));
        p1.ObjectIds.ShouldBe(["a", "c"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2, token: p1.ContinuationToken));
        p2.ObjectIds.ShouldBe(["e"]);
        p2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_surfaces_for_a_concrete_user_not_otherwise_granted()
    {
        var (auth, _, t) = await SetupAsync("ilo-wild",
            [Tup("doc", "gamma", "editor", new SubjectRef("user", "*"))],
            [("user:*", "edit", "doc", "gamma", false)]);

        var result = await auth.ListObjectsAsync(Req(t, "nobody"));
        result.ObjectIds.ShouldBe(["gamma"]);
    }
}
