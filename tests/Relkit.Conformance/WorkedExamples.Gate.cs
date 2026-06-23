using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Conformance;

public static partial class WorkedExamples
{
    // 12.5 — "only quarantine-trained vets may access animals in a quarantine enclosure".
    // animal.access = (can_access - enclosure->is_quarantine)
    //               + (enclosure->is_quarantine & (vet_member + vet_nurse_member) & trained_member)
    private static Schema QuarantineSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("enclosure", t => t
            .Relation("is_quarantine", s => s.Wildcard("user"))
            .Permission("is_quarantine", p => p.Relation("is_quarantine")))
        .Type("animal", t => t
            .Relation("can_access", s => s.User().SubjectSet("group", "member"))
            .Relation("enclosure", s => s.Type("enclosure"))
            .Relation("vet_member", s => s.SubjectSet("group", "member"))
            .Relation("vet_nurse_member", s => s.SubjectSet("group", "member"))
            .Relation("trained_member", s => s.SubjectSet("group", "member"))
            .Permission("access", p => p
                .Union(b => b.Relation("can_access").Exclude(x => x.Arrow("enclosure", "is_quarantine")))
                .Union(b => b
                    .Arrow("enclosure", "is_quarantine")
                    .Intersect(x => x.Relation("vet_member").Union(y => y.Relation("vet_nurse_member")))
                    .Intersect(x => x.Relation("trained_member")))))
        .Build();

    private static RelationTuple[] QuarantineMembership(string animal) =>
    [
        T("animal", animal, "vet_member", new SubjectRef("group", "vets", "member")),
        T("animal", animal, "trained_member", new SubjectRef("group", "trained", "member")),
        T("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        T("group", "vets", "member", new SubjectRef("user", "jones")),
        T("group", "trained", "member", new SubjectRef("user", "dr-smith")),
        T("animal", animal, "can_access", new SubjectRef("user", "dr-smith")),
        T("animal", animal, "can_access", new SubjectRef("user", "jones")),
    ];

    public static ConformanceCase QuarantineTrainedVetAllowed()
    {
        var tuples = new List<RelationTuple>(QuarantineMembership("EL-001"))
        {
            T("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            T("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        return new ConformanceCase("12.5 trained vet inside quarantine allowed", QuarantineSchema(),
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-001"), "access", new SubjectRef("user", "dr-smith"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    public static ConformanceCase QuarantineUntrainedVetDenied()
    {
        var tuples = new List<RelationTuple>(QuarantineMembership("EL-001"))
        {
            T("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            T("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        return new ConformanceCase("12.5 untrained vet inside quarantine denied", QuarantineSchema(),
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-001"), "access", new SubjectRef("user", "jones"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: false);
    }

    public static ConformanceCase OutsideQuarantineBaseAccess()
    {
        var tuples = new List<RelationTuple>(QuarantineMembership("EL-002"))
        {
            T("animal", "EL-002", "enclosure", new SubjectRef("enclosure", "KH1")),   // not quarantine
        };
        return new ConformanceCase("12.5 outside quarantine base access passes", QuarantineSchema(),
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-002"), "access", new SubjectRef("user", "jones"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.6 — time-bounded dispensing. The condition rides on the dispenser tuple.
    // In M0 the NullConditionEvaluator treats within_hours as satisfied, so this asserts
    // the structural grant resolves and the conditioned branch is reached.
    public static ConformanceCase TimeBoundedDispenseWithinHours()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("category", t => t
                .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
                .Permission("record_dispense", p => p.Relation("dispenser")))
            .Condition("within_hours", c => c.Int("start").Int("end"))
            .Build();

        var tuples = new[]
        {
            new RelationTuple(new EntityRef("category", "drugs"), "dispenser",
                new SubjectRef("group", "vets", "member"),
                new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 })),
            T("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        };

        var context = new Dictionary<string, object?>();
        return new ConformanceCase("12.6 time-bounded dispense (in hours, null-eval)", schema,
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("category", "drugs"), "record_dispense", new SubjectRef("user", "dr-smith"),
            new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero), context, Expected: true);
    }
}
