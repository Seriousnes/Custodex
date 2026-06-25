using Dapper;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Evaluation;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ObjectRowRecomputerTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Recompute_returns_the_objects_structural_rows_honouring_exclusion()
    {
        var t = new TenantContext("recomp", "t");
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
                Tup("doc", "alpha", "editor", new SubjectRef("group", "team", "member")),
                Tup("doc", "alpha", "blocked", new SubjectRef("user", "bob")),
                Tup("group", "team", "member", new SubjectRef("user", "alice")),
                Tup("group", "team", "member", new SubjectRef("user", "bob")),
            ], [], u);
            await u.CommitAsync();
        }

        var recomputer = new ObjectRowRecomputer(_relations, _attributes);
        var rows = await recomputer.RecomputeAsync(new SchemaIndex(Build()), _schemas, t,
            new EntityRef("doc", "alpha"), ["alice", "bob"]);

        rows.Select(r => $"{r.Subject}|{r.ObjectId}").ShouldBe(["user:alice|alpha"]);
        rows[0].Conditioned.ShouldBeFalse();
    }
}
