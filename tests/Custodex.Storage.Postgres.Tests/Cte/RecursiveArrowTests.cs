using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class RecursiveArrowTests(PostgresFixture fx) : IAsyncLifetime
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

    [Fact]
    public async Task A_self_referential_arrow_resolves_nested_ancestry_on_the_cte_path()
    {
        var schema = new SchemaBuilder("v1").Type("folder", t => t
            .Relation("owner", s => s.User())
            .Relation("parent", s => s.Type("folder"))
            .Permission("view", p => p.Relation("owner").Union(x => x.Arrow("parent", "view")))).Build();

        _t = new TenantContext("recursive-arrow", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = _t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING", new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(_t.Store, schema, u);
            await _relations.WriteAsync(_t,
            [
                new RelationTuple(new EntityRef("folder", "C"), "parent", new SubjectRef("folder", "B")),
                new RelationTuple(new EntityRef("folder", "B"), "parent", new SubjectRef("folder", "A")),
                new RelationTuple(new EntityRef("folder", "A"), "owner", new SubjectRef("user", "u")),
            ], [], u);
            await u.CommitAsync();
        }

        var auth = new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());

        (await auth.CheckAsync(Req(new EntityRef("folder", "A"), "view", "u"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("folder", "B"), "view", "u"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("folder", "C"), "view", "u"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("folder", "C"), "view", "stranger"))).Allowed.ShouldBeFalse();
    }

    private CheckRequest Req(EntityRef obj, string perm, string subjectId) => new(
        _t, obj, perm, new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId), new Dictionary<string, object?>()));
}
