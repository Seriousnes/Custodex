using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Conformance;

public static partial class WorkedExamples
{
    private static Schema TimeBoundedSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("category", t => t
            .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
            .Permission("record_dispense", p => p.Relation("dispenser")))
        .Condition("within_hours",
            p => p.Int("start").Int("end"),
            b => b.And(
                b.Ge(b.Hour(b.Now()), b.Param("start")),
                b.Lt(b.Hour(b.Now()), b.Param("end"))))
        .Build();

    private static RelationTuple[] TimeBoundedTuples() =>
    [
        new RelationTuple(new EntityRef("category", "drugs"), "dispenser",
            new SubjectRef("group", "vets", "member"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 })),
        T("group", "vets", "member", new SubjectRef("user", "dr-smith")),
    ];

    // In hours (10:00): within_hours satisfied -> dispenser grant resolves -> allow.
    public static ConformanceCase TimeBoundedDispenseRealInHours() =>
        new("12.6 time-bounded dispense (in hours, real eval)", TimeBoundedSchema(), TimeBoundedTuples(),
            Array.Empty<AttributeSeed>(),
            new EntityRef("category", "drugs"), "record_dispense", new SubjectRef("user", "dr-smith"),
            new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero), new Dictionary<string, object?>(), Expected: true);

    // Out of hours (20:00): within_hours fails -> conditioned tuple skipped -> deny.
    public static ConformanceCase TimeBoundedDispenseRealOutOfHours() =>
        new("12.6 time-bounded dispense (out of hours, real eval)", TimeBoundedSchema(), TimeBoundedTuples(),
            Array.Empty<AttributeSeed>(),
            new EntityRef("category", "drugs"), "record_dispense", new SubjectRef("user", "dr-smith"),
            new DateTimeOffset(2026, 6, 23, 20, 0, 0, TimeSpan.Zero), new Dictionary<string, object?>(), Expected: false);
}
