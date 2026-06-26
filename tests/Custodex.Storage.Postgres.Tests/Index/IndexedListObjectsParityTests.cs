using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedListObjectsParityTests(PostgresFixture fx) : IAsyncLifetime
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

    private async Task<(IndexedAuthorizer Indexed, NpgsqlCteAuthorizer Oracle, TenantContext T)> SetupAsync(
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

        var oracle = new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
        return (new IndexedAuthorizer(oracle, _index, _schemas), oracle, t);
    }

    private static ListObjectsRequest Req(TenantContext t, string user, int pageSize, string? token = null) => new(
        t, new SubjectRef("user", user), "doc", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private static async Task<List<string>> PageAllAsync(IAuthorizer auth, TenantContext t, string user, int pageSize)
    {
        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await auth.ListObjectsAsync(Req(t, user, pageSize, token));
            all.AddRange(page.ObjectIds);
            token = page.ContinuationToken;
        } while (token is not null);
        return all;
    }

    [Fact]
    public async Task Index_path_equals_cte_oracle_across_exclusion_and_conditioned_drops()
    {
        var (indexed, oracle, t) = await SetupAsync("parity",
            [Tup("doc", "alpha", "editor", new SubjectRef("group", "team", "member")),
             Tup("doc", "beta",  "editor", new SubjectRef("group", "team", "member")),
             Tup("doc", "beta",  "blocked", new SubjectRef("user", "alice")),
             Tup("doc", "delta", "editor", new SubjectRef("user", "alice")),
             Tup("group", "team", "member", new SubjectRef("user", "alice"))],
            [("user:alice", "edit", "doc", "alpha", false),
             ("user:alice", "edit", "doc", "beta",  true),
             ("user:alice", "edit", "doc", "delta", false)]);

        var viaIndex  = await PageAllAsync(indexed, t, "alice", pageSize: 2);
        var viaOracle = await PageAllAsync(oracle,  t, "alice", pageSize: 2);

        viaIndex.ShouldBe(viaOracle);
        viaIndex.ShouldBe(["alpha", "delta"]);
    }
}
