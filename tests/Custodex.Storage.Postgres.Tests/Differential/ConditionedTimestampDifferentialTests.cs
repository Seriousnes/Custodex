using Custodex.Abstractions;
using Custodex.Core;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Differential;

[Collection("postgres")]
public class ConditionedTimestampDifferentialTests(PostgresFixture fx) : IAsyncLifetime
{
    private static readonly DateTimeOffset Reference = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

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
    [InlineData("attr", true)]
    [InlineData("attr", false)]
    [InlineData("param", true)]
    [InlineData("param", false)]
    public async Task Timestamp_condition_on_deep_membership_matches_oracle(string kind, bool fresh)
    {
        var until = fresh ? Reference.AddDays(1) : Reference.AddDays(-1);

        var builder = new SchemaBuilder($"ct-{kind}")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")));

        builder = kind == "attr"
            ? builder.Condition("gate", _ => { }, b => b.Le(b.Now(), b.Attribute("expires")))
            : builder.Condition("gate", p => p.Timestamp("until"), b => b.Le(b.Now(), b.Param("until")));

        var schema = builder.Build();

        var gate = kind == "attr"
            ? new ConditionRef("gate", new Dictionary<string, object?>())
            : new ConditionRef("gate", new Dictionary<string, object?> { ["until"] = until });

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", Member("g1")),
            TC("group", "g1", "member", U("u1"), gate),
        };

        var attributes = kind == "attr"
            ? new (EntityRef, IReadOnlyDictionary<string, object?>)[]
              {
                  (new EntityRef("group", "g1"), new Dictionary<string, object?> { ["expires"] = until }),
              }
            : [];

        var model = new GeneratedModel(schema, tuples, attributes, [new EntityRef("doc", "o1")], [U("u1")]);

        var store = $"ct-{kind}-{(fresh ? "fresh" : "lapsed")}";
        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
        var tenant = new TenantContext(store, "t");
        var ctx = new RequestContext(Reference, U("u1"), new Dictionary<string, object?>());
        var req = new CheckRequest(tenant, new EntityRef("doc", "o1"), "view", U("u1"), ctx);

        var oracleAllowed = (await oracle.CheckAsync(req)).Allowed;
        var cteAllowed = (await cte.CheckAsync(req)).Allowed;

        oracleAllowed.ShouldBe(fresh);
        cteAllowed.ShouldBe(oracleAllowed);
    }
}
