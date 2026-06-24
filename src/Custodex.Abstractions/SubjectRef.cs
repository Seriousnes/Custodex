namespace Custodex.Abstractions;

public readonly record struct SubjectRef(string Type, string Id, string? Relation = null)
{
    public bool IsSubjectSet => Relation is not null;
    public bool IsWildcard => Id == "*";
    public override string ToString() => Relation is null ? $"{Type}:{Id}" : $"{Type}:{Id}#{Relation}";
}
