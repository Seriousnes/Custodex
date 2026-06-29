using Custodex.Abstractions;
using Custodex.Core;

namespace Custodex.TestKit.Conformance;

/// <summary>
/// The hand-asserted conformance corpus: self-contained scenarios spanning the permission algebra, each
/// with expected Check, ListObjects, and ListSubjects results fixed by hand. Every execution path runs
/// this same corpus, so each is validated against ground truth rather than against another path.
/// </summary>
public static class ConformanceCorpus
{
    /// <summary>Every scenario in the corpus.</summary>
    public static IReadOnlyList<ConformanceScenario> All { get; } =
    [
        DirectRelation(),
        Union(),
        Intersection(),
        Exclusion(),
        ArrowSingleHop(),
        ArrowRecursive(),
        GroupNesting(),
        Wildcard(),
        PermissionReference(),
        PermissionLayeringChain(),
        NameCollisionRelationWins(),
        PrecedenceFlat(),
        ConditionAttributeGate(),
    ];

    /// <summary>Resolves a scenario by its unique <see cref="ConformanceScenario.Name"/>.</summary>
    public static ConformanceScenario ByName(string name) =>
        All.Single(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    private static RelationTuple T(string objType, string objId, string relation, SubjectRef subject) =>
        new(new EntityRef(objType, objId), relation, subject);

    private static SubjectRef U(string id) => new("user", id);

    private static SubjectRef Member(string groupId) => new("group", groupId, "member");

    private static SubjectRef Wild() => new("user", "*");

    private static EntityRef Obj(string type, string id) => new(type, id);

    private static ConformanceScenario DirectRelation()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Permission("view", p => p.Relation("viewer")))
            .Build();

        return new ConformanceScenario("direct-relation", schema,
            [T("doc", "d1", "viewer", U("u1"))],
            [],
            [
                new CheckExpectation("holder", Obj("doc", "d1"), "view", U("u1"), true),
                new CheckExpectation("non-holder", Obj("doc", "d1"), "view", U("u2"), false),
            ],
            [
                new ListObjectsExpectation("u1-views", U("u1"), "doc", "view", ["d1"]),
                new ListObjectsExpectation("u2-views-none", U("u2"), "doc", "view", []),
            ],
            [new ListSubjectsExpectation("viewers", Obj("doc", "d1"), "view", [U("u1")])]);
    }

    private static ConformanceScenario Union()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("a", s => s.User())
                .Relation("b", s => s.User())
                .Permission("access", p => p.Relation("a").Union(u => u.Relation("b"))))
            .Build();

        return new ConformanceScenario("union", schema,
            [T("doc", "d1", "a", U("u1")), T("doc", "d1", "b", U("u2"))],
            [],
            [
                new CheckExpectation("via-a", Obj("doc", "d1"), "access", U("u1"), true),
                new CheckExpectation("via-b", Obj("doc", "d1"), "access", U("u2"), true),
                new CheckExpectation("neither", Obj("doc", "d1"), "access", U("u3"), false),
            ],
            [
                new ListObjectsExpectation("u1", U("u1"), "doc", "access", ["d1"]),
                new ListObjectsExpectation("u3-none", U("u3"), "doc", "access", []),
            ],
            [new ListSubjectsExpectation("holders", Obj("doc", "d1"), "access", [U("u1"), U("u2")])]);
    }

    private static ConformanceScenario Intersection()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("a", s => s.User())
                .Relation("b", s => s.User())
                .Permission("gate", p => p.Relation("a").Intersect(x => x.Relation("b"))))
            .Build();

        return new ConformanceScenario("intersection", schema,
            [T("doc", "d1", "a", U("u1")), T("doc", "d1", "b", U("u1")), T("doc", "d1", "a", U("u2"))],
            [],
            [
                new CheckExpectation("both", Obj("doc", "d1"), "gate", U("u1"), true),
                new CheckExpectation("only-a", Obj("doc", "d1"), "gate", U("u2"), false),
                new CheckExpectation("neither", Obj("doc", "d1"), "gate", U("u3"), false),
            ],
            [],
            [new ListSubjectsExpectation("both-holders", Obj("doc", "d1"), "gate", [U("u1")])]);
    }

    private static ConformanceScenario Exclusion()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
            .Build();

        return new ConformanceScenario("exclusion", schema,
            [T("doc", "d1", "viewer", U("u1")), T("doc", "d1", "viewer", U("u2")), T("doc", "d1", "blocked", U("u2"))],
            [],
            [
                new CheckExpectation("not-blocked", Obj("doc", "d1"), "view", U("u1"), true),
                new CheckExpectation("blocked", Obj("doc", "d1"), "view", U("u2"), false),
                new CheckExpectation("non-viewer", Obj("doc", "d1"), "view", U("u3"), false),
            ],
            [
                new ListObjectsExpectation("u1", U("u1"), "doc", "view", ["d1"]),
                new ListObjectsExpectation("u2-blocked", U("u2"), "doc", "view", []),
            ],
            [new ListSubjectsExpectation("unblocked-viewers", Obj("doc", "d1"), "view", [U("u1")])]);
    }

    private static ConformanceScenario ArrowSingleHop()
    {
        var schema = new SchemaBuilder("v1")
            .Type("folder", t => t
                .Relation("editor", s => s.User())
                .Permission("edit", p => p.Relation("editor")))
            .Type("doc", t => t
                .Relation("parent", s => s.Type("folder"))
                .Relation("editor", s => s.User())
                .Permission("edit", p => p.Relation("editor").Union(u => u.Arrow("parent", "edit"))))
            .Build();

        return new ConformanceScenario("arrow-single-hop", schema,
            [
                T("folder", "f1", "editor", U("u1")),
                T("doc", "d1", "parent", new SubjectRef("folder", "f1")),
                T("doc", "d1", "editor", U("u2")),
            ],
            [],
            [
                new CheckExpectation("inherited-from-parent", Obj("doc", "d1"), "edit", U("u1"), true),
                new CheckExpectation("direct-editor", Obj("doc", "d1"), "edit", U("u2"), true),
                new CheckExpectation("stranger", Obj("doc", "d1"), "edit", U("u3"), false),
                new CheckExpectation("folder-editor", Obj("folder", "f1"), "edit", U("u1"), true),
            ],
            [
                new ListObjectsExpectation("u1-docs", U("u1"), "doc", "edit", ["d1"]),
                new ListObjectsExpectation("u2-docs", U("u2"), "doc", "edit", ["d1"]),
                new ListObjectsExpectation("u3-none", U("u3"), "doc", "edit", []),
            ],
            [new ListSubjectsExpectation("doc-editors", Obj("doc", "d1"), "edit", [U("u1"), U("u2")])]);
    }

    private static ConformanceScenario ArrowRecursive()
    {
        var schema = new SchemaBuilder("v1")
            .Type("folder", t => t
                .Relation("owner", s => s.User())
                .Relation("parent", s => s.Type("folder"))
                .Permission("view", p => p.Relation("owner").Union(u => u.Arrow("parent", "view"))))
            .Build();

        return new ConformanceScenario("arrow-recursive", schema,
            [
                T("folder", "f1", "owner", U("u1")),
                T("folder", "f2", "parent", new SubjectRef("folder", "f1")),
                T("folder", "f3", "parent", new SubjectRef("folder", "f2")),
            ],
            [],
            [
                new CheckExpectation("root", Obj("folder", "f1"), "view", U("u1"), true),
                new CheckExpectation("one-up", Obj("folder", "f2"), "view", U("u1"), true),
                new CheckExpectation("two-up", Obj("folder", "f3"), "view", U("u1"), true),
                new CheckExpectation("stranger", Obj("folder", "f3"), "view", U("u2"), false),
            ],
            [new ListObjectsExpectation("u1-folders", U("u1"), "folder", "view", ["f1", "f2", "f3"])],
            [new ListSubjectsExpectation("deep-viewers", Obj("folder", "f3"), "view", [U("u1")])]);
    }

    private static ConformanceScenario GroupNesting()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t
                .Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))
            .Build();

        return new ConformanceScenario("group-nesting", schema,
            [
                T("doc", "d1", "viewer", Member("g1")),
                T("group", "g1", "member", Member("g2")),
                T("group", "g2", "member", U("u1")),
                T("group", "g1", "member", U("u2")),
            ],
            [],
            [
                new CheckExpectation("nested-member", Obj("doc", "d1"), "view", U("u1"), true),
                new CheckExpectation("direct-member", Obj("doc", "d1"), "view", U("u2"), true),
                new CheckExpectation("non-member", Obj("doc", "d1"), "view", U("u3"), false),
            ],
            [new ListObjectsExpectation("nested-member-docs", U("u1"), "doc", "view", ["d1"])],
            [new ListSubjectsExpectation("all-members", Obj("doc", "d1"), "view", [U("u1"), U("u2")])]);
    }

    private static ConformanceScenario Wildcard()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.Wildcard("user").User())
                .Permission("view", p => p.Relation("viewer")))
            .Build();

        return new ConformanceScenario("wildcard", schema,
            [T("doc", "d1", "viewer", Wild()), T("doc", "d2", "viewer", U("u1"))],
            [],
            [
                new CheckExpectation("wildcard-grants-u1", Obj("doc", "d1"), "view", U("u1"), true),
                new CheckExpectation("wildcard-grants-u2", Obj("doc", "d1"), "view", U("u2"), true),
                new CheckExpectation("direct-only", Obj("doc", "d2"), "view", U("u1"), true),
                new CheckExpectation("not-direct", Obj("doc", "d2"), "view", U("u2"), false),
            ],
            [
                new ListObjectsExpectation("u1-both", U("u1"), "doc", "view", ["d1", "d2"]),
                new ListObjectsExpectation("u2-wildcard-only", U("u2"), "doc", "view", ["d1"]),
            ],
            [
                new ListSubjectsExpectation("wildcard-subject", Obj("doc", "d1"), "view", [Wild()]),
                new ListSubjectsExpectation("direct-subject", Obj("doc", "d2"), "view", [U("u1")]),
            ]);
    }

    private static ConformanceScenario PermissionReference()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("editor", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked")))
                .Permission("view", p => p.Relation("edit")))
            .Build();

        return new ConformanceScenario("permission-reference", schema,
            [T("doc", "d1", "editor", U("u1")), T("doc", "d1", "editor", U("u2")), T("doc", "d1", "blocked", U("u2"))],
            [],
            [
                new CheckExpectation("view-through-edit", Obj("doc", "d1"), "view", U("u1"), true),
                new CheckExpectation("view-blocked-via-edit", Obj("doc", "d1"), "view", U("u2"), false),
                new CheckExpectation("view-stranger", Obj("doc", "d1"), "view", U("u3"), false),
                new CheckExpectation("edit-direct", Obj("doc", "d1"), "edit", U("u1"), true),
            ],
            [
                new ListObjectsExpectation("u1-views", U("u1"), "doc", "view", ["d1"]),
                new ListObjectsExpectation("u2-blocked", U("u2"), "doc", "view", []),
            ],
            [
                new ListSubjectsExpectation("viewers-through-edit", Obj("doc", "d1"), "view", [U("u1")]),
                new ListSubjectsExpectation("editors", Obj("doc", "d1"), "edit", [U("u1")]),
            ]);
    }

    private static ConformanceScenario PermissionLayeringChain()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("holder", s => s.User())
                .Permission("a", p => p.Relation("holder"))
                .Permission("b", p => p.Relation("a"))
                .Permission("c", p => p.Relation("b")))
            .Build();

        return new ConformanceScenario("permission-layering-chain", schema,
            [T("doc", "d1", "holder", U("u1"))],
            [],
            [
                new CheckExpectation("c-through-chain", Obj("doc", "d1"), "c", U("u1"), true),
                new CheckExpectation("c-stranger", Obj("doc", "d1"), "c", U("u2"), false),
                new CheckExpectation("b-through-chain", Obj("doc", "d1"), "b", U("u1"), true),
                new CheckExpectation("a-direct", Obj("doc", "d1"), "a", U("u1"), true),
            ],
            [new ListObjectsExpectation("u1-c", U("u1"), "doc", "c", ["d1"])],
            [new ListSubjectsExpectation("c-holders", Obj("doc", "d1"), "c", [U("u1")])]);
    }

    private static ConformanceScenario NameCollisionRelationWins()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("dup", s => s.User())
                .Relation("absent", s => s.User())
                .Permission("dup", p => p.Relation("absent"))
                .Permission("gate", p => p.Relation("dup")))
            .Build();

        return new ConformanceScenario("name-collision-relation-wins", schema,
            [T("doc", "d1", "dup", U("u1"))],
            [],
            [
                new CheckExpectation("relation-wins", Obj("doc", "d1"), "gate", U("u1"), true),
                new CheckExpectation("stranger", Obj("doc", "d1"), "gate", U("u2"), false),
            ],
            [],
            [new ListSubjectsExpectation("gate-holders", Obj("doc", "d1"), "gate", [U("u1")])]);
    }

    private static ConformanceScenario PrecedenceFlat()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("a", s => s.User())
                .Relation("b", s => s.User())
                .Relation("c", s => s.User())
                .Permission("p", p => p.Relation("a").Union(u => u.Relation("b")).Intersect(x => x.Relation("c"))))
            .Build();

        return new ConformanceScenario("precedence-flat", schema,
            [
                T("doc", "d1", "a", U("u1")), T("doc", "d1", "c", U("u1")),
                T("doc", "d1", "a", U("u2")),
                T("doc", "d1", "c", U("u3")),
                T("doc", "d1", "b", U("u4")), T("doc", "d1", "c", U("u4")),
            ],
            [],
            [
                new CheckExpectation("a-and-c", Obj("doc", "d1"), "p", U("u1"), true),
                new CheckExpectation("a-without-c", Obj("doc", "d1"), "p", U("u2"), false),
                new CheckExpectation("c-without-ab", Obj("doc", "d1"), "p", U("u3"), false),
                new CheckExpectation("b-and-c", Obj("doc", "d1"), "p", U("u4"), true),
            ],
            [],
            [new ListSubjectsExpectation("p-holders", Obj("doc", "d1"), "p", [U("u1"), U("u4")])]);
    }

    private static ConformanceScenario ConditionAttributeGate()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Permission("view", p => p.Relation("viewer").Conditioned("active")))
            .Condition("active", _ => { }, b => b.Eq(b.Attribute("flag"), b.Const(true)))
            .Build();

        return new ConformanceScenario("condition-attribute-gate", schema,
            [T("doc", "d1", "viewer", U("u1")), T("doc", "d2", "viewer", U("u1"))],
            [
                new AttributeSeed(Obj("doc", "d1"), new Dictionary<string, object?> { ["flag"] = true }),
                new AttributeSeed(Obj("doc", "d2"), new Dictionary<string, object?> { ["flag"] = false }),
            ],
            [
                new CheckExpectation("flag-on", Obj("doc", "d1"), "view", U("u1"), true),
                new CheckExpectation("flag-off", Obj("doc", "d2"), "view", U("u1"), false),
                new CheckExpectation("non-viewer", Obj("doc", "d1"), "view", U("u2"), false),
            ],
            [new ListObjectsExpectation("u1-active-only", U("u1"), "doc", "view", ["d1"])],
            [
                new ListSubjectsExpectation("active-viewers", Obj("doc", "d1"), "view", [U("u1")]),
                new ListSubjectsExpectation("inactive-none", Obj("doc", "d2"), "view", []),
            ]);
    }
}
