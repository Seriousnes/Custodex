using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.SqlServer.Index;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Index;

/// <summary>Pins the load-bearing correctness property of the maintained index: a condition reached
/// through unconditioned subject-set edges must mark the index row <c>Conditioned</c> (so it is
/// re-checked), not returned blind. The grant for <c>user:leaf</c> on <c>doc:d1#view</c> traverses
/// the unconditioned <c>viewer → g1 → g2</c> chain and meets a condition only at the deepest edge.</summary>
[Collection("sqlserver")]
public class DeepConditionIndexTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);
    private static readonly TenantContext T = new("deep-cond", "t");

    private static Schema BuildSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member"))
            .Permission("view", p => p.Relation("viewer")))
        .Condition("gate", _ => { }, b => b.Eq(b.Attribute("flag"), b.Const(true)))
        .Build();

    private static RelationTuple T1(string ot, string oid, string rel, SubjectRef s, ConditionRef? c = null) =>
        new(new EntityRef(ot, oid), rel, s, c);

    private async Task SetFlagAsync(EntityRef obj, bool flag)
    {
        var attributes = new SqlServerAttributeStore(fx.ConnectionString);
        await using var u = await Factory.BeginAsync();
        await attributes.SetAsync(T, obj, new Dictionary<string, object?> { ["flag"] = flag }, u);
        await u.CommitAsync();
    }

    private static async Task<HashSet<string>> ListAsync(IAuthorizer auth, SubjectRef subject)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>());
            var page = await auth.ListObjectsAsync(
                new ListObjectsRequest(T, subject, "doc", "view", ctx, PageSize: 10, ContinuationToken: token));
            foreach (var id in page.ObjectIds) ids.Add(id);
            token = page.ContinuationToken;
        } while (token is not null);
        return ids;
    }

    [Fact]
    public async Task Deep_condition_through_unconditioned_edges_is_marked_and_rechecked()
    {
        await Seed.TenantAsync(Factory, T);

        var schemas = new SqlServerSchemaStore(fx.ConnectionString);
        var relations = new SqlServerRelationStore(fx.ConnectionString);
        var attributes = new SqlServerAttributeStore(fx.ConnectionString);
        var index = new SqlServerIndexStore(fx.ConnectionString);

        await using (var u = await Factory.BeginAsync())
        {
            await schemas.SetActiveAsync(T.Store, BuildSchema(), u);
            await relations.WriteAsync(T,
            [
                T1("doc", "d1", "viewer", new SubjectRef("group", "g1", "member")),
                T1("group", "g1", "member", new SubjectRef("group", "g2", "member")),
                T1("group", "g2", "member", new SubjectRef("user", "leaf"),
                    new ConditionRef("gate", new Dictionary<string, object?>())),
            ], [], u);
            await u.CommitAsync();
        }

        var rebuilder = new ReverseIndexRebuilder(fx.ConnectionString, schemas, relations, attributes, index);
        await using (var u = await Factory.BeginAsync())
        {
            await rebuilder.RebuildAsync(T, u);
            await u.CommitAsync();
        }

        var rows = await index.ReadForObjectAsync(T, "v1", "doc", "d1");
        var leafRow = rows.ShouldHaveSingleItem();
        leafRow.Subject.ShouldBe("user:leaf");
        leafRow.Permission.ShouldBe("view");
        leafRow.Conditioned.ShouldBeTrue();

        var cte = new SqlServerCteAuthorizer(fx.ConnectionString, schemas, attributes, new CelConditionEvaluator());
        var indexed = new IndexedAuthorizer(cte, index, schemas);
        var leaf = new SubjectRef("user", "leaf");

        await SetFlagAsync(new EntityRef("group", "g2"), false);
        (await ListAsync(indexed, leaf)).ShouldBeEmpty();

        await SetFlagAsync(new EntityRef("group", "g2"), true);
        (await ListAsync(indexed, leaf)).ShouldBe(["d1"]);
    }
}
