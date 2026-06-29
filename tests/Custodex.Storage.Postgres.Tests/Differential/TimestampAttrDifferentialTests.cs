using Custodex.Abstractions;
using Custodex.Core;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Differential;

[Collection("postgres")]
public class TimestampAttrDifferentialTests(PostgresFixture fx) : IAsyncLifetime
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

    private static RelationTuple TC(string ot, string oid, string rel, SubjectRef s, ConditionRef c) =>
        new(new EntityRef(ot, oid), rel, s, c);

    [Theory]
    [InlineData("same", true)]
    [InlineData("different", false)]
    public async Task Declared_timestamp_attributes_compared_attr_vs_attr_match_oracle(string shape, bool expected)
    {
        var a = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var b = shape == "same"
            ? new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.FromHours(1))
            : new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var schema = new SchemaBuilder($"ta-{shape}")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))
            .Condition("gate", _ => { },
                b2 => b2.Eq(
                    b2.Attribute("a", ConditionType.Timestamp),
                    b2.Attribute("b", ConditionType.Timestamp)))
            .Build();

        var gate = new ConditionRef("gate", new Dictionary<string, object?>());
        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u1"), gate),
        };
        var attributes = new (EntityRef, IReadOnlyDictionary<string, object?>)[]
        {
            (new EntityRef("group", "g1"), new Dictionary<string, object?> { ["a"] = a, ["b"] = b }),
        };
        var model = new GeneratedModel(schema, tuples, attributes, [new EntityRef("doc", "o1")], [U("u1")]);

        var store = $"ta-{shape}";
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
