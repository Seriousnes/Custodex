namespace Custodex.Abstractions;

public readonly record struct EntityRef(string Type, string Id)
{
    public bool IsWildcard => Id == "*";
    public override string ToString() => $"{Type}:{Id}";
}
