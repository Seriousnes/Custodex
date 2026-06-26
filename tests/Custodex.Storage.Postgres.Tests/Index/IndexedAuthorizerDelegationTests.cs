using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedAuthorizerDelegationTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlIndexStore _index = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member"))
            .Permission("view", p => p.Relation("viewer")))
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

    private async Task<(IndexedAuthorizer Auth, TenantContext T)> SetupAsync(string store, params RelationTuple[] tuples)
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
        var inner = new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
        return (new IndexedAuthorizer(inner, _index, _schemas), t);
    }

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Check_delegates_to_the_inner_cte_authorizer()
    {
        var (auth, t) = await SetupAsync("idx-check",
            Tup("doc", "D1", "viewer", new SubjectRef("user", "alice")));

        var result = await auth.CheckAsync(new CheckRequest(
            t, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>())));

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ListSubjects_delegates_to_the_inner_cte_authorizer()
    {
        var (auth, t) = await SetupAsync("idx-ls",
            Tup("doc", "D1", "viewer", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "bob")));

        var result = await auth.ListSubjectsAsync(new ListSubjectsRequest(
            t, new EntityRef("doc", "D1"), "view",
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>())));

        result.Subjects.Select(s => s.Id).ShouldBe(["alice", "bob"]);
    }
}
