using CsCheck;

using Custodex.Abstractions;
using Custodex.Core;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>A generated model combining a schema, relation tuples, attribute values,
/// and pre-selected probe entities and subjects for the differential harness.</summary>
public sealed record GeneratedModel(
    Schema Schema,
    IReadOnlyList<RelationTuple> Tuples,
    IReadOnlyList<(EntityRef Obj, IReadOnlyDictionary<string, object?> Attrs)> Attributes,
    IReadOnlyList<EntityRef> ProbeObjects,
    IReadOnlyList<SubjectRef> ProbeSubjects);

/// <summary>Generates random valid <see cref="GeneratedModel"/> instances drawn from a curated
/// set of schema skeletons that exercise union, exclusion, arrow traversal, nested groups,
/// inner-exclusion-through-arrow, and wildcard intersection gates.</summary>
public static class ModelGenerator
{
    private static readonly string[] Users = ["u1", "u2", "u3", "u4"];
    private static readonly string[] Groups = ["g1", "g2", "g3"];

    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static readonly CsCheck.Gen<string> UserGen =
        CsCheck.Gen.OneOfConst(Users);

    private static readonly CsCheck.Gen<string> GroupIdGen =
        CsCheck.Gen.OneOfConst(Groups);

    /// <summary>S1: doc.view = viewer - blocked; viewer accepts user, group#member, or wildcard user:*.
    /// Three docs (d1/d2/d3) all share the same viewer group to exercise ListObjects pagination at
    /// PageSize:2. A two-hop nested group chain (og→ig→leafU) exercises depth-2 subject-set
    /// traversal: <c>group:og#member@group:ig#member</c>, <c>group:ig#member@user:leafU</c>.</summary>
    private static Schema S1Schema() => new SchemaBuilder("s1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
        .Build();

    private static readonly CsCheck.Gen<GeneratedModel> S1 =
        CsCheck.Gen.Select(
            CsCheck.Gen.Bool,
            UserGen,
            UserGen,
            GroupIdGen,
            UserGen,
            UserGen,
            UserGen,
            (wildcard, viewU, blockU, vg, m1, m2, leafU) =>
            {
                var tuples = new List<RelationTuple>
                {
                    T("doc", "d1", "viewer", new SubjectRef("group", vg, "member")),
                    T("doc", "d2", "viewer", new SubjectRef("group", vg, "member")),
                    T("doc", "d3", "viewer", new SubjectRef("group", vg, "member")),
                    T("group", vg, "member", new SubjectRef("user", m1)),
                    T("group", vg, "member", new SubjectRef("user", m2)),
                    T("doc", "d1", "viewer", new SubjectRef("user", viewU)),
                    T("doc", "d1", "blocked", new SubjectRef("user", blockU)),
                    T("group", vg, "member", new SubjectRef("group", "og", "member")),
                    T("group", "og", "member", new SubjectRef("group", "ig", "member")),
                    T("group", "ig", "member", new SubjectRef("user", leafU)),
                };
                if (wildcard) tuples.Add(T("doc", "d1", "viewer", new SubjectRef("user", "*")));
                return new GeneratedModel(S1Schema(), tuples, [],
                    [new EntityRef("doc", "d1"), new EntityRef("doc", "d2"), new EntityRef("doc", "d3")],
                    [.. Users.Select(u => new SubjectRef("user", u))]);
            });

    /// <summary>S2: asset.edit = crate->edit; crate.edit = editor - blocked (inner-exclusion-through-arrow shape).</summary>
    private static Schema S2Schema() => new SchemaBuilder("s2")
        .Type("crate", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("asset", t => t
            .Relation("crate", s => s.Type("crate"))
            .Permission("edit", p => p.Arrow("crate", "edit")))
        .Build();

    private static readonly CsCheck.Gen<GeneratedModel> S2 =
        CsCheck.Gen.Select(
            CsCheck.Gen.OneOfConst("a1", "a2"),
            CsCheck.Gen.OneOfConst("e1", "e2"),
            UserGen,
            UserGen,
            UserGen,
            (assetId, crateId, editor, blocked, editor2) =>
            {
                List<RelationTuple> tuples =
                [
                    T("asset", assetId, "crate", new SubjectRef("crate", crateId)),
                    T("crate", crateId, "editor", new SubjectRef("user", editor)),
                    T("crate", crateId, "editor", new SubjectRef("user", editor2)),
                    T("crate", crateId, "blocked", new SubjectRef("user", blocked)),
                ];
                return new GeneratedModel(S2Schema(), tuples, [],
                    [new EntityRef("asset", assetId)],
                    [.. Users.Select(u => new SubjectRef("user", u))]);
            });

    /// <summary>S3: intersection-through-arrow wildcard gate; access requires either a direct grant outside
    /// a flagged crate, or both group memberships when inside a flagged crate.</summary>
    private static Schema S3Schema() => new SchemaBuilder("s3")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("crate", t => t
            .Relation("is_quarantine", s => s.Wildcard("user"))
            .Permission("is_quarantine", p => p.Relation("is_quarantine")))
        .Type("asset", t => t
            .Relation("can_access", s => s.User().SubjectSet("group", "member"))
            .Relation("crate", s => s.Type("crate"))
            .Relation("vet_member", s => s.SubjectSet("group", "member"))
            .Relation("trained_member", s => s.SubjectSet("group", "member"))
            .Permission("access", p => p
                .Union(b => b.Relation("can_access").Exclude(x => x.Arrow("crate", "is_quarantine")))
                .Union(b => b.Arrow("crate", "is_quarantine")
                    .Intersect(x => x.Relation("vet_member"))
                    .Intersect(x => x.Relation("trained_member")))))
        .Build();

    private static readonly CsCheck.Gen<GeneratedModel> S3 =
        CsCheck.Gen.Select(
            CsCheck.Gen.Bool,
            UserGen,
            UserGen,
            UserGen,
            (flagged, vet1, vet2, trained) =>
            {
                var tuples = new List<RelationTuple>
                {
                    T("asset", "an", "crate", new SubjectRef("crate", "en")),
                    T("asset", "an", "vet_member", new SubjectRef("group", "gv", "member")),
                    T("asset", "an", "trained_member", new SubjectRef("group", "tr", "member")),
                    T("asset", "an", "can_access", new SubjectRef("user", vet1)),
                    T("group", "gv", "member", new SubjectRef("user", vet1)),
                    T("group", "gv", "member", new SubjectRef("user", vet2)),
                    T("group", "tr", "member", new SubjectRef("user", trained)),
                };
                if (flagged) tuples.Add(T("crate", "en", "is_quarantine", new SubjectRef("user", "*")));
                return new GeneratedModel(S3Schema(), tuples, [],
                    [new EntityRef("asset", "an")],
                    [.. Users.Select(u => new SubjectRef("user", u))]);
            });

    /// <summary>S4: subject-set nesting under non-group/member identifiers: type <c>team</c> with
    /// relation <c>owner</c>, and type <c>repo</c> with relation <c>reader</c> (accepts user or
    /// <c>team#owner</c>) and permission <c>read = reader</c>. A two-hop nest
    /// (<c>team:t1#owner@team:t2#owner</c>, <c>team:t2#owner@user:leafU</c>) exercises the
    /// generic subject-set climb with identifiers unrelated to groups or membership.</summary>
    private static Schema S4Schema() => new SchemaBuilder("s4")
        .Type("team", t => t.Relation("owner", s => s.User().SubjectSet("team", "owner")))
        .Type("repo", t => t
            .Relation("reader", s => s.User().SubjectSet("team", "owner"))
            .Permission("read", p => p.Relation("reader")))
        .Build();

    private static readonly CsCheck.Gen<GeneratedModel> S4 =
        CsCheck.Gen.Select(
            UserGen,
            UserGen,
            (directU, leafU) =>
            {
                List<RelationTuple> tuples =
                [
                    T("repo", "r1", "reader", new SubjectRef("team", "t1", "owner")),
                    T("repo", "r2", "reader", new SubjectRef("team", "t1", "owner")),
                    T("repo", "r1", "reader", new SubjectRef("user", directU)),
                    T("team", "t1", "owner", new SubjectRef("team", "t2", "owner")),
                    T("team", "t2", "owner", new SubjectRef("user", leafU)),
                ];
                return new GeneratedModel(S4Schema(), tuples, [],
                    [new EntityRef("repo", "r1"), new EntityRef("repo", "r2")],
                    [.. Users.Select(u => new SubjectRef("user", u))]);
            });

    /// <summary>A <see cref="CsCheck.Gen{T}"/> that randomly selects one of the four curated
    /// skeleton schemas and randomizes its relation-tuple data.</summary>
    public static readonly CsCheck.Gen<GeneratedModel> Gen =
        CsCheck.Gen.OneOf(S1, S2, S3, S4);
}
