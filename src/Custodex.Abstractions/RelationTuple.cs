namespace Custodex.Abstractions;

public sealed record ConditionRef(string Name, IReadOnlyDictionary<string, object?> Parameters);

public sealed record RelationTuple(
    EntityRef Object,
    string Relation,
    SubjectRef Subject,
    ConditionRef? Condition = null);
