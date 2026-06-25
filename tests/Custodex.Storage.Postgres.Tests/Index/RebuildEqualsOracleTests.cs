using Dapper;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class RebuildEqualsOracleTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlIndexStore _index = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
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

    [Fact]
    public async Task Rebuilt_index_rows_equal_the_oracle_list_objects_per_subject()
    {
        var t = new TenantContext("rb-oracle", "t");
        RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
                new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
                new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, Build(), u);
            await _relations.WriteAsync(t,
            [
                Tup("doc", "alpha", "editor", new SubjectRef("user", "alice")),
                Tup("doc", "alpha", "editor", new SubjectRef("group", "team", "member")),
                Tup("doc", "beta", "editor", new SubjectRef("group", "team", "member")),
                Tup("doc", "beta", "blocked", new SubjectRef("user", "alice")),
                Tup("group", "team", "member", new SubjectRef("user", "alice")),
                Tup("group", "team", "member", new SubjectRef("user", "bob")),
            ], [], u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        var oracle = new EngineDrivenAuthorizer(_schemas, _relations, _attributes, new NullConditionEvaluator());
        foreach (var user in new[] { "alice", "bob" })
        {
            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>());
            var oracleIds = (await oracle.ListObjectsAsync(new ListObjectsRequest(
                t, new SubjectRef("user", user), "doc", "edit", ctx, PageSize: 1000))).ObjectIds.OrderBy(x => x);
            var indexIds = (await _index.QueryObjectsAsync(t, "v1", $"user:{user}", "edit", "doc", 1000, null))
                .Select(r => r.ObjectId).OrderBy(x => x);
            indexIds.ShouldBe(oracleIds, $"index must equal oracle for {user}");
        }
    }
}
