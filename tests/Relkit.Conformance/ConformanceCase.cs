using Relkit.Abstractions;

namespace Relkit.Conformance;

/// <summary>Synced resource attributes to seed for one object before the query runs.</summary>
public sealed record AttributeSeed(EntityRef Object, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// One declarative authorization decision: load <see cref="Schema"/>, <see cref="Tuples"/>
/// and <see cref="Attributes"/>, then Check (<see cref="Subject"/>, <see cref="Object"/>,
/// <see cref="Permission"/>) under <see cref="Now"/> + <see cref="Context"/>, expecting
/// <see cref="Expected"/>.
/// </summary>
public sealed record ConformanceCase(
    string Name,
    Schema Schema,
    IReadOnlyList<RelationTuple> Tuples,
    IReadOnlyList<AttributeSeed> Attributes,
    EntityRef Object,
    string Permission,
    SubjectRef Subject,
    DateTimeOffset Now,
    IReadOnlyDictionary<string, object?> Context,
    bool Expected);
