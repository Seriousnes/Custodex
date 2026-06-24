using Custodex.Abstractions;

namespace Custodex.Core.Tests.Conformance;

public sealed record AttributeSeed(EntityRef Object, IReadOnlyDictionary<string, object?> Attributes);

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
