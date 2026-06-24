using Custodex.Abstractions;
using Custodex.Core;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Hand-picked model instances used by smoke tests and other harness-level fixtures.
/// Each sample exercises a specific algebra shape so a test failure is directly attributable
/// to a known structural interaction.</summary>
internal static class ModelGeneratorSamples
{
    /// <summary>Returns the S2 discriminator schema: <c>asset.edit = crate->edit</c>;
    /// <c>crate.edit = editor - blocked</c>. This exercises inner-exclusion propagated
    /// through an arrow traversal.</summary>
    internal static Schema S2DiscriminatorSchema() => new SchemaBuilder("s2")
        .Type("crate", t => t
            .Relation("editor", s => s.User())
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Type("asset", t => t
            .Relation("crate", s => s.Type("crate"))
            .Permission("edit", p => p.Arrow("crate", "edit")))
        .Build();

    /// <summary>Returns the tuples for the S2 discriminator model: <c>carol</c> is both editor
    /// and blocked on crate <c>e1</c>, so <c>asset.edit</c> denies carol; <c>dana</c> is
    /// editor-only and is therefore allowed.</summary>
    internal static IReadOnlyList<RelationTuple> S2DiscriminatorTuples() =>
    [
        new(new EntityRef("asset", "a1"), "crate", new SubjectRef("crate", "e1")),
        new(new EntityRef("crate", "e1"), "editor", new SubjectRef("user", "carol")),
        new(new EntityRef("crate", "e1"), "blocked", new SubjectRef("user", "carol")),
        new(new EntityRef("crate", "e1"), "editor", new SubjectRef("user", "dana")),
    ];
}
