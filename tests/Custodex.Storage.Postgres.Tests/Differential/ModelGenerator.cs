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

    /// <summary>S1: doc.view = viewer - blocked; viewer accepts user, group#member, or wildcard user:*.</summary>
    private static Schema S1Schema() => new SchemaBuilder("s1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
        .Build();

    private static readonly CsCheck.Gen<GeneratedModel> S1 =
        CsCheck.Gen.Select(
            CsCheck.Gen.OneOfConst("d1", "d2", "d3"),
            CsCheck.Gen.Bool,
            UserGen,
            UserGen,
            GroupIdGen,
            UserGen,
            UserGen,
            (docId, wildcard, viewU, blockU, vg, m1, m2) =>
            {
                var tuples = new List<RelationTuple>
                {
                    T("doc", docId, "viewer", new SubjectRef("group", vg, "member")),
                    T("group", vg, "member", new SubjectRef("user", m1)),
                    T("group", vg, "member", new SubjectRef("user", m2)),
                    T("doc", docId, "viewer", new SubjectRef("user", viewU)),
                    T("doc", docId, "blocked", new SubjectRef("user", blockU)),
                };
                if (wildcard) tuples.Add(T("doc", docId, "viewer", new SubjectRef("user", "*")));
                return new GeneratedModel(S1Schema(), tuples, [],
                    [new EntityRef("doc", docId)],
                    Users.Select(u => new SubjectRef("user", u)).ToList());
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
                    Users.Select(u => new SubjectRef("user", u)).ToList());
            });

    /// <summary>S3: intersection-through-arrow wildcard gate; access requires either direct grant outside
    /// a flagged crate, or vet+trained membership when inside a flagged crate.</summary>
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
                    Users.Select(u => new SubjectRef("user", u)).ToList());
            });

    /// <summary>A <see cref="CsCheck.Gen{T}"/> that randomly selects one of the three curated
    /// skeleton schemas and randomizes its relation-tuple data.</summary>
    public static readonly CsCheck.Gen<GeneratedModel> Gen =
        CsCheck.Gen.OneOf(S1, S2, S3);
}
