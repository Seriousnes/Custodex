using Custodex.Abstractions;
using Custodex.Core;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>A conditioned-exclusion shape whose exclusion is gated by a condition comparing two
/// declared-<see cref="ConditionType.Timestamp"/> attributes, both stored as ISO-8601 strings. The
/// deciding object's two encodings are chosen so their ordinal string order is the opposite of their
/// chronological order, so the indexed answer matches the oracle only when the gate row is re-checked
/// (the coarse marking keeps it) and the comparison resolves chronologically rather than by ordinal
/// string compare.</summary>
public static class TimestampExclusionModels
{
    private static SubjectRef U(string id) => new("user", id);

    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static (EntityRef, IReadOnlyDictionary<string, object?>) Window(string oid, string blockedUntil, string checkpoint) =>
        (new EntityRef("doc", oid), new Dictionary<string, object?>
        {
            ["blockedUntil"] = blockedUntil,
            ["checkpoint"] = checkpoint,
        });

    private static IReadOnlySet<string> Set(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.Ordinal);

    /// <summary>Returns the timestamp-gated conditioned-exclusion cases.</summary>
    public static IReadOnlyList<ExclusionCase> All() => [BlockWindowOverTypedTimestamps()];

    private static ExclusionCase BlockWindowOverTypedTimestamps()
    {
        var schema = new SchemaBuilder("tex")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked").Conditioned("window"))))
            .Condition("window", _ => { }, b => b.Ge(
                b.Attribute("blockedUntil", ConditionType.Timestamp),
                b.Attribute("checkpoint", ConditionType.Timestamp)))
            .Build();

        var tuples = new List<RelationTuple>
        {
            T("doc", "o1", "viewer", U("u1")),
            T("doc", "o1", "blocked", U("u1")),
            T("doc", "o2", "viewer", U("u1")),
            T("doc", "o2", "blocked", U("u1")),
            T("doc", "o3", "viewer", U("u1")),
        };

        var attributes = new (EntityRef, IReadOnlyDictionary<string, object?>)[]
        {
            Window("o1", "2024-03-01T23:00:00+09:00", "2024-03-01T20:00:00+00:00"),
            Window("o2", "2024-05-01T12:00:00+00:00", "2024-05-01T08:00:00+00:00"),
        };

        var probeObjects = new[] { new EntityRef("doc", "o1"), new EntityRef("doc", "o2"), new EntityRef("doc", "o3") };
        var model = new GeneratedModel(schema, tuples, attributes, probeObjects, [U("u1")]);
        return new ExclusionCase("block-window-over-typed-timestamps", model, "doc", "view",
            [(U("u1"), Set("o1", "o3"))]);
    }
}
