using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteBatchAndExplainTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlCteAuthorizer _auth = null!;
    private static readonly TenantContext T = new("cte", "batch");

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        var relations = new NpgsqlRelationStore(fx.ConnectionString);
        var schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);

        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t.Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))
            .Build();

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = T.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await schemas.SetActiveAsync(T.Store, schema, u);
        await relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "crew", "member")),
            new RelationTuple(new EntityRef("doc", "D2"), "viewer", new SubjectRef("user", "bob")),
            new RelationTuple(new EntityRef("group", "crew"), "member", new SubjectRef("user", "alice")),
        ], [], u);
        await u.CommitAsync();
        _auth = new NpgsqlCteAuthorizer(fx.ConnectionString, schemas, attributes, new NullConditionEvaluator());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Batch_returns_a_result_per_item_in_order()
    {
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>());
        var req = new BatchCheckRequest(T,
        [
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")),
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob")),
            new CheckItem(new EntityRef("doc", "D2"), "view", new SubjectRef("user", "bob")),
        ], ctx);

        var results = await _auth.BatchCheckAsync(req);
        results.Count.ShouldBe(3);
        results[0].Allowed.ShouldBeTrue();
        results[1].Allowed.ShouldBeFalse();
        results[2].Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Explain_returns_a_populated_trace()
    {
        var req = new CheckRequest(T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>()),
            Explain: true);
        var result = await _auth.CheckAsync(req);
        result.Allowed.ShouldBeTrue();
        result.Explain.ShouldNotBeNull();
        result.Explain!.Description.ShouldContain("doc:D1");
    }
}
