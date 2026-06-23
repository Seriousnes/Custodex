using Custodex.Abstractions;
using Custodex.Core;

namespace Custodex.Conformance;

public static partial class WorkedExamples
{
    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    // 12.1 — "vets may record drug dispensing" via a resource category.
    // inventory_item.record_dispense = dispenser + category->record_dispense - blocked
    public static ConformanceCase RoleGrantOverCategory()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("category", t => t
                .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("record_dispense", p => p.Relation("dispenser").Exclude(x => x.Relation("blocked"))))
            .Type("inventory_item", t => t
                .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
                .Relation("category", s => s.Type("category"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("record_dispense", p => p
                    .Relation("dispenser")
                    .Arrow("category", "record_dispense")
                    .Exclude(x => x.Relation("blocked"))))
            .Build();

        var tuples = new[]
        {
            T("category", "drugs", "dispenser", new SubjectRef("group", "vets", "member")),
            T("inventory_item", "vaccine-X", "category", new SubjectRef("category", "drugs")),
            T("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        };

        return new ConformanceCase("12.1 role grant over category", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("inventory_item", "vaccine-X"), "record_dispense", new SubjectRef("user", "dr-smith"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.2 — "the macropods round may edit a hand-picked species list".
    public static ConformanceCase TeamGrantOverCuratedSet()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("species", t => t
                .Relation("editor", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("editor")))
            .Build();

        var tuples = new[]
        {
            T("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            T("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            T("group", "macropods", "member", new SubjectRef("user", "alice")),
        };

        return new ConformanceCase("12.2 team grant over curated set", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("species", "kangaroo"), "edit", new SubjectRef("user", "alice"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.3 — "keepers edit Sydney enclosures, not other sites".
    // enclosure.edit = can_edit + site->edit - blocked
    public static ConformanceCase SiteScopedAccess()
    {
        var schema = SiteScopedSchema();
        var tuples = new[]
        {
            T("site", "sydney", "can_edit", new SubjectRef("group", "keepers", "member")),
            T("enclosure", "KH1", "site", new SubjectRef("site", "sydney")),
            T("group", "keepers", "member", new SubjectRef("user", "kim")),
        };
        return new ConformanceCase("12.3 site-scoped access (sydney allowed)", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("enclosure", "KH1"), "edit", new SubjectRef("user", "kim"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.3 negative — Melbourne enclosure is untouched by the Sydney grant.
    public static ConformanceCase SiteScopedAccessOtherSiteDenied()
    {
        var schema = SiteScopedSchema();
        var tuples = new[]
        {
            T("site", "sydney", "can_edit", new SubjectRef("group", "keepers", "member")),
            T("enclosure", "MEL1", "site", new SubjectRef("site", "melbourne")),
            T("group", "keepers", "member", new SubjectRef("user", "kim")),
        };
        return new ConformanceCase("12.3 site-scoped access (melbourne denied)", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("enclosure", "MEL1"), "edit", new SubjectRef("user", "kim"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: false);
    }

    private static Schema SiteScopedSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("site", t => t
            .Relation("can_edit", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("can_edit")))
        .Type("enclosure", t => t
            .Relation("can_edit", s => s.User().SubjectSet("group", "member"))
            .Relation("site", s => s.Type("site"))
            .Relation("blocked", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p
                .Relation("can_edit")
                .Arrow("site", "edit")
                .Exclude(x => x.Relation("blocked"))))
        .Build();

    // 12.4 — direct grant on one instance.
    public static ConformanceCase DirectGrantOnInstance()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("can_manage", s => s.User())
                .Permission("manage", p => p.Relation("can_manage")))
            .Build();
        var tuples = new[] { T("animal", "EL-001", "can_manage", new SubjectRef("user", "carol")) };
        return new ConformanceCase("12.4 direct grant on instance", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-001"), "manage", new SubjectRef("user", "carol"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }
}
