using Custodex.Abstractions;

namespace Custodex.TestKit.Conformance;

/// <summary>Object attributes seeded for a scenario, read by conditions during evaluation.</summary>
public sealed record AttributeSeed(EntityRef Object, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>One hand-asserted Check expectation: the expected <see cref="Expected"/> result of checking
/// <see cref="Subject"/> for <see cref="Permission"/> on <see cref="Object"/>.</summary>
public sealed record CheckExpectation(
    string Name,
    EntityRef Object,
    string Permission,
    SubjectRef Subject,
    bool Expected,
    DateTimeOffset? Now = null,
    IReadOnlyDictionary<string, object?>? Context = null);

/// <summary>One hand-asserted ListObjects expectation: the exact set of object ids of
/// <see cref="ObjectType"/> on which <see cref="Subject"/> holds <see cref="Permission"/>.</summary>
public sealed record ListObjectsExpectation(
    string Name,
    SubjectRef Subject,
    string ObjectType,
    string Permission,
    IReadOnlyList<string> ExpectedIds,
    DateTimeOffset? Now = null,
    IReadOnlyDictionary<string, object?>? Context = null);

/// <summary>One hand-asserted ListSubjects expectation: the exact set of subjects that hold
/// <see cref="Permission"/> on <see cref="Object"/>.</summary>
public sealed record ListSubjectsExpectation(
    string Name,
    EntityRef Object,
    string Permission,
    IReadOnlyList<SubjectRef> ExpectedSubjects,
    DateTimeOffset? Now = null,
    IReadOnlyDictionary<string, object?>? Context = null);

/// <summary>
/// A self-contained, hand-asserted authorization scenario: a schema, its tuples and attributes, and the
/// expected Check, ListObjects, and ListSubjects results. Every expectation is fixed by hand, independent
/// of any implementation, so the same scenario is ground truth for every execution path that runs it.
/// </summary>
public sealed record ConformanceScenario(
    string Name,
    Schema Schema,
    IReadOnlyList<RelationTuple> Tuples,
    IReadOnlyList<AttributeSeed> Attributes,
    IReadOnlyList<CheckExpectation> Checks,
    IReadOnlyList<ListObjectsExpectation> ListObjects,
    IReadOnlyList<ListSubjectsExpectation> ListSubjects);
