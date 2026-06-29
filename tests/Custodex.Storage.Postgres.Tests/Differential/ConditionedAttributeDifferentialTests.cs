using Custodex.Abstractions;
using Custodex.Core;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Differential;

[Collection("postgres")]
public class ConditionedAttributeDifferentialTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static SubjectRef U(string id) => new("user", id);
    private static SubjectRef Member(string id) => new("group", id, "member");

    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static RelationTuple TC(string ot, string oid, string rel, SubjectRef s, string condition) =>
        new(new EntityRef(ot, oid), rel, s, new ConditionRef(condition, new Dictionary<string, object?>()));

    public static IEnumerable<object[]> Cases() =>
    [
        ["bool", true, true],
        ["bool", false, false],
        ["string", "open", true],
        ["string", "shut", false],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Attribute_condition_on_deep_membership_matches_oracle(string kind, object value, bool expected)
    {
        var builder = new SchemaBuilder($"ca-{kind}")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")));

        builder = kind == "bool"
            ? builder.Condition("gate", _ => { }, b => b.Eq(b.Attribute("flag"), b.Const(true)))
            : builder.Condition("gate", _ => { }, b => b.Eq(b.Attribute("flag"), b.Const("open")));

        var schema = builder.Build();
        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u1"), "gate"),
        };
        var attributes = new (EntityRef, IReadOnlyDictionary<string, object?>)[]
        {
            (new EntityRef("group", "g1"), new Dictionary<string, object?> { ["flag"] = value }),
        };
        var model = new GeneratedModel(schema, tuples, attributes, [new EntityRef("doc", "o1")], [U("u1")]);

        var store = $"ca-{kind}-{value}";
        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
        var tenant = new TenantContext(store, "t");
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, U("u1"), new Dictionary<string, object?>());
        var req = new CheckRequest(tenant, new EntityRef("doc", "o1"), "view", U("u1"), ctx);

        var oracleAllowed = (await oracle.CheckAsync(req)).Allowed;
        var cteAllowed = (await cte.CheckAsync(req)).Allowed;

        oracleAllowed.ShouldBe(expected);
        cteAllowed.ShouldBe(oracleAllowed);
    }
}
