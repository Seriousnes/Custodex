using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteMembershipTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private static readonly TenantContext T = new("cte", "membership");

    private static Schema GroupSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
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

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = T.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(T.Store, GroupSchema(), u);
        await u.CommitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private NpgsqlCteAuthorizer NewAuthorizer() =>
        new(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());

    private static CheckRequest Req(string subjectId) => new(
        T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId), new Dictionary<string, object?>()));

    private async Task WriteAsync(params RelationTuple[] tuples)
    {
        await using var u = await _factory.BeginAsync();
        await _relations.WriteAsync(T, tuples, [], u);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Direct_user_grant_matches()
    {
        await WriteAsync(new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));
        var auth = NewAuthorizer();
        (await auth.CheckAsync(Req("alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req("bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Wildcard_grant_matches_everyone()
    {
        await WriteAsync(new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "*")));
        (await NewAuthorizer().CheckAsync(Req("anyone"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Nested_group_membership_resolves_transitively()
    {
        await WriteAsync(
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "staff", "member")),
            new RelationTuple(new EntityRef("group", "staff"), "member", new SubjectRef("group", "crew", "member")),
            new RelationTuple(new EntityRef("group", "crew"), "member", new SubjectRef("user", "pat")));
        (await NewAuthorizer().CheckAsync(Req("pat"))).Allowed.ShouldBeTrue();
        (await NewAuthorizer().CheckAsync(Req("outsider"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Group_membership_cycle_prunes_to_deny_without_throwing()
    {
        await WriteAsync(
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "a", "member")),
            new RelationTuple(new EntityRef("group", "a"), "member", new SubjectRef("group", "b", "member")),
            new RelationTuple(new EntityRef("group", "b"), "member", new SubjectRef("group", "a", "member")));
        (await NewAuthorizer().CheckAsync(Req("ghost"))).Allowed.ShouldBeFalse();
    }
}
