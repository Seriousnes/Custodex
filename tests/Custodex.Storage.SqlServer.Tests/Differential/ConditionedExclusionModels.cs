using Custodex.Abstractions;
using Custodex.Core;

namespace Custodex.Storage.SqlServer.Tests.Differential;

/// <summary>One conditioned-exclusion shape: a model plus the object ids each probe subject must
/// resolve for the probed permission, so a test can assert the oracle answer and that the indexed
/// answer equals it.</summary>
public sealed record ExclusionCase(
    string Name,
    GeneratedModel Model,
    string ObjectType,
    string Permission,
    IReadOnlyList<(SubjectRef Subject, IReadOnlySet<string> Expected)> Expectations);

/// <summary>Handcrafted conditioned-exclusion models that discriminate a correct coarse index
/// recompute from the over-denying baseline and from unsound touch-delta or forced-deny variants.
/// Conditions are constant (<c>block</c> = false) so no attributes are required.</summary>
public static class ConditionedExclusionModels
{
    private static SubjectRef U(string id) => new("user", id);
    private static SubjectRef Member(string id) => new("group", id, "member");

    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static RelationTuple TC(string ot, string oid, string rel, SubjectRef s, string condition) =>
        new(new EntityRef(ot, oid), rel, s, new ConditionRef(condition, new Dictionary<string, object?>()));

    private static IReadOnlySet<string> Set(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.Ordinal);

    /// <summary>Returns the discriminating conditioned-exclusion cases.</summary>
    public static IReadOnlyList<ExclusionCase> All() => [DeepConditionInRight(), NestedRightOverInclusion(), SharedConditionedSubPermission()];

    /// <summary>Projects a model's tuples into an add-only write sequence so the reverse index is
    /// built one maintained transaction at a time, exercising the incremental maintainer over the
    /// conditioned-exclusion shape rather than stripping its exclusion edges.</summary>
    public static IReadOnlyList<WriteOp> AddOps(GeneratedModel model)
    {
        var ops = new List<WriteOp>(model.Tuples.Count);
        foreach (var t in model.Tuples)
            ops.Add(new WriteOp(Add: true, t));
        return ops;
    }

    private static ExclusionCase DeepConditionInRight()
    {
        var schema = new SchemaBuilder("cea")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
            .Condition("block", _ => { }, x => x.Const(false))
            .Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", U("u1")),
            T("doc", "o1", "blocked", Member("g1")),
            TC("group", "g1", "member", U("u1"), "block"),
        };

        var model = new GeneratedModel(schema, tuples, [], [new EntityRef("doc", "o1")], [U("u1")]);
        return new ExclusionCase("deep-condition-in-right", model, "doc", "view",
            [(U("u1"), Set("o1"))]);
    }

    private static ExclusionCase NestedRightOverInclusion()
    {
        var schema = new SchemaBuilder("ceb")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("blockedC", s => s.User())
                .Relation("blockedD", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")
                    .Exclude(x => x.Relation("blockedC").Union(u => u.Relation("blockedD")))))
            .Condition("block", _ => { }, x => x.Const(false))
            .Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", U("u1")),
            T("doc", "o1", "viewer", U("u2")),
            T("doc", "o1", "blockedC", U("u1")),
            T("doc", "o1", "blockedD", Member("g1")),
            TC("group", "g1", "member", U("u1"), "block"),
        };

        var model = new GeneratedModel(schema, tuples, [], [new EntityRef("doc", "o1")], [U("u1"), U("u2")]);
        return new ExclusionCase("nested-right-over-inclusion", model, "doc", "view",
            [(U("u1"), Set()), (U("u2"), Set("o1"))]);
    }

    private static ExclusionCase SharedConditionedSubPermission()
    {
        var schema = new SchemaBuilder("cec")
            .Type("res", t => t
                .Relation("viewer", s => s.User())
                .Permission("q", p => p.Relation("viewer").Conditioned("block")))
            .Type("doc", t => t
                .Relation("link", s => s.Type("res"))
                .Relation("other", s => s.User())
                .Relation("blockedElse", s => s.User())
                .Permission("p", x => x.Arrow("link", "q").Union(u => u.Relation("other"))
                    .Exclude(e => e.Arrow("link", "q").Union(u => u.Relation("blockedElse")))))
            .Condition("block", _ => { }, x => x.Const(false))
            .Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "link", new SubjectRef("res", "r1")),
            T("doc", "o1", "other", U("u1")),
            T("res", "r1", "viewer", U("u1")),
        };

        var model = new GeneratedModel(schema, tuples, [], [new EntityRef("doc", "o1")], [U("u1")]);
        return new ExclusionCase("shared-conditioned-sub-permission", model, "doc", "p",
            [(U("u1"), Set("o1"))]);
    }
}
