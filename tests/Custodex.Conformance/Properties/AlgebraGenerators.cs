using CsCheck;
using Custodex.Abstractions;
using Custodex.Core;

namespace Custodex.Conformance.Properties;

public static class AlgebraGenerators
{
    public static readonly TenantContext Tenant = new("prop", "t");

    // A small pool of user ids the generators draw from.
    public static readonly Gen<string> UserId = Gen.OneOf(
        Gen.Const("u1"), Gen.Const("u2"), Gen.Const("u3"), Gen.Const("u4"), Gen.Const("u5"));

    // A schema with an exclusion-free, condition-free permission for the monotonicity law.
    public static Schema MonotoneSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Permission("view", p => p.Relation("viewer")))
        .Build();

    // A schema whose permission is a - a (self-exclusion).
    public static Schema SelfExcludeSchema() => new SchemaBuilder("v1")
        .Type("doc", t => t
            .Relation("viewer", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("viewer"))))
        .Build();

    // A chain of nested groups g0 < g1 < ... < gN, with a user at the bottom and a grant at the top.
    public static readonly Gen<int> ChainLength = Gen.Int[1, 6];

    public static IReadOnlyList<RelationTuple> NestedChainTuples(int length, string user)
    {
        var tuples = new List<RelationTuple>
        {
            // doc:D1#viewer@group:g0#member
            new(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "g0", "member")),
        };
        for (var i = 0; i < length - 1; i++)
            tuples.Add(new RelationTuple(new EntityRef("group", $"g{i}"), "member",
                new SubjectRef("group", $"g{i + 1}", "member")));
        // bottom group gets the user
        tuples.Add(new RelationTuple(new EntityRef("group", $"g{length - 1}"), "member",
            new SubjectRef("user", user)));
        return tuples;
    }
}
