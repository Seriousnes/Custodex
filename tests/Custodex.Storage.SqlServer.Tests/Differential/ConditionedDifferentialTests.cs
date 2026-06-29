using Custodex.Abstractions;
using Custodex.Core;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Differential;

[Collection("sqlserver")]
public class ConditionedDifferentialTests(SqlServerFixture fx)
{
    private const string Pass = "pass";
    private const string Block = "block";

    private static SubjectRef U(string id) => new("user", id);
    private static SubjectRef Member(string id) => new("group", id, "member");

    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static RelationTuple TC(string ot, string oid, string rel, SubjectRef s, string condition) =>
        new(new EntityRef(ot, oid), rel, s, new ConditionRef(condition, new Dictionary<string, object?>()));

    private static SchemaBuilder Gated(SchemaBuilder b) => b
        .Condition(Pass, _ => { }, x => x.Const(true))
        .Condition(Block, _ => { }, x => x.Const(false));

    private static IReadOnlyList<RelationTuple> MembershipChain(
        string ownerType, string ownerId, string relation, string position, string condition) => position switch
    {
        "direct" => [TC(ownerType, ownerId, relation, U("u1"), condition)],
        "deep" =>
        [
            T(ownerType, ownerId, relation, Member("g1")),
            TC("group", "g1", "member", U("u1"), condition),
        ],
        "intermediate" =>
        [
            T(ownerType, ownerId, relation, Member("g1")),
            TC("group", "g1", "member", Member("g2"), condition),
            T("group", "g2", "member", U("u1")),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
    };

    private static (GeneratedModel Model, EntityRef Obj, string Permission) BuildModel(
        string algebra, string position, string condition)
    {
        var tuples = new List<RelationTuple>();
        Schema schema;
        EntityRef obj;
        string permission;

        switch (algebra)
        {
            case "union":
                schema = Gated(new SchemaBuilder("cu")
                    .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
                    .Type("doc", t => t
                        .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                        .Relation("other", s => s.User())
                        .Permission("view", p => p.Relation("viewer").Union(x => x.Relation("other"))))).Build();
                tuples.AddRange(MembershipChain("doc", "o1", "viewer", position, condition));
                obj = new EntityRef("doc", "o1");
                permission = "view";
                break;

            case "intersect":
                schema = Gated(new SchemaBuilder("ci")
                    .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
                    .Type("res", t => t
                        .Relation("a", s => s.User().SubjectSet("group", "member"))
                        .Relation("b", s => s.User())
                        .Permission("grant", p => p.Relation("a").Intersect(x => x.Relation("b"))))).Build();
                tuples.Add(T("res", "o1", "b", U("u1")));
                tuples.AddRange(MembershipChain("res", "o1", "a", position, condition));
                obj = new EntityRef("res", "o1");
                permission = "grant";
                break;

            case "exclude":
                schema = Gated(new SchemaBuilder("ce")
                    .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
                    .Type("doc", t => t
                        .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                        .Relation("blocked", s => s.User())
                        .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))).Build();
                tuples.AddRange(MembershipChain("doc", "o1", "viewer", position, condition));
                obj = new EntityRef("doc", "o1");
                permission = "view";
                break;

            case "arrow":
                schema = Gated(new SchemaBuilder("ca")
                    .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
                    .Type("res", t => t
                        .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                        .Permission("see", p => p.Relation("viewer")))
                    .Type("doc", t => t
                        .Relation("link", s => s.Type("res"))
                        .Permission("view", p => p.Arrow("link", "see")))).Build();
                tuples.Add(T("doc", "o1", "link", new SubjectRef("res", "r1")));
                tuples.AddRange(MembershipChain("res", "r1", "viewer", position, condition));
                obj = new EntityRef("doc", "o1");
                permission = "view";
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(algebra), algebra, null);
        }

        var model = new GeneratedModel(schema, tuples, [], [obj], [U("u1")]);
        return (model, obj, permission);
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var algebra in new[] { "union", "intersect", "exclude", "arrow" })
        foreach (var position in new[] { "direct", "deep", "intermediate" })
        foreach (var (condition, expected) in new[] { (Pass, true), (Block, false) })
            yield return [algebra, position, condition, expected];
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Cte_matches_oracle_for_conditioned_position_across_algebra(
        string algebra, string position, string condition, bool expected)
    {
        var (model, obj, permission) = BuildModel(algebra, position, condition);
        var store = $"cd-{algebra}-{position}-{condition}";
        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
        var tenant = new TenantContext(store, "t");
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, U("u1"), new Dictionary<string, object?>());
        var req = new CheckRequest(tenant, obj, permission, U("u1"), ctx);

        var oracleAllowed = (await oracle.CheckAsync(req)).Allowed;
        var cteAllowed = (await cte.CheckAsync(req)).Allowed;

        oracleAllowed.ShouldBe(expected);
        cteAllowed.ShouldBe(oracleAllowed);
    }

    [Fact]
    public async Task Deep_leaf_condition_false_through_unconditioned_edge_denies()
    {
        var schema = Gated(new SchemaBuilder("cx")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))).Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u1"), Block),
        };
        var model = new GeneratedModel(schema, tuples, [], [new EntityRef("doc", "o1")], [U("u1")]);
        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, "cx-deny");
        var tenant = new TenantContext("cx-deny", "t");
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, U("u1"), new Dictionary<string, object?>());
        var req = new CheckRequest(tenant, new EntityRef("doc", "o1"), "view", U("u1"), ctx);

        (await oracle.CheckAsync(req)).Allowed.ShouldBeFalse();
        (await cte.CheckAsync(req)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Deep_leaf_condition_true_through_unconditioned_edge_allows()
    {
        var schema = Gated(new SchemaBuilder("cx")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))).Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u1"), Pass),
        };
        var model = new GeneratedModel(schema, tuples, [], [new EntityRef("doc", "o1")], [U("u1")]);
        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, "cx-allow");
        var tenant = new TenantContext("cx-allow", "t");
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, U("u1"), new Dictionary<string, object?>());
        var req = new CheckRequest(tenant, new EntityRef("doc", "o1"), "view", U("u1"), ctx);

        (await oracle.CheckAsync(req)).Allowed.ShouldBeTrue();
        (await cte.CheckAsync(req)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Unconditioned_sibling_path_still_allows_when_conditioned_sibling_denies()
    {
        var schema = Gated(new SchemaBuilder("cm")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))).Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", U("u1")),
            T("doc", "o1", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u1"), Block),
        };
        var model = new GeneratedModel(schema, tuples, [], [new EntityRef("doc", "o1")], [U("u1")]);
        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, "cm-mixed");
        var tenant = new TenantContext("cm-mixed", "t");
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, U("u1"), new Dictionary<string, object?>());
        var req = new CheckRequest(tenant, new EntityRef("doc", "o1"), "view", U("u1"), ctx);

        (await oracle.CheckAsync(req)).Allowed.ShouldBeTrue();
        (await cte.CheckAsync(req)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Batch_matches_oracle_over_conditioned_chain()
    {
        var schema = Gated(new SchemaBuilder("cb")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))).Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u1"), Block),
            T("doc", "o2", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u2"), Pass),
        };
        var model = new GeneratedModel(schema, tuples, [],
            [new EntityRef("doc", "o1"), new EntityRef("doc", "o2")], [U("u1"), U("u2")]);
        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, "cb-batch");
        var tenant = new TenantContext("cb-batch", "t");
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, U("u1"), new Dictionary<string, object?>());

        var items = new List<CheckItem>
        {
            new(new EntityRef("doc", "o1"), "view", U("u1")),
            new(new EntityRef("doc", "o2"), "view", U("u2")),
            new(new EntityRef("doc", "o1"), "view", U("u2")),
        };
        var batch = new BatchCheckRequest(tenant, items, ctx);

        var oracleResults = await oracle.BatchCheckAsync(batch);
        var cteResults = await cte.BatchCheckAsync(batch);

        cteResults.Count.ShouldBe(oracleResults.Count);
        for (var i = 0; i < oracleResults.Count; i++)
            cteResults[i].Allowed.ShouldBe(oracleResults[i].Allowed);
    }
}
