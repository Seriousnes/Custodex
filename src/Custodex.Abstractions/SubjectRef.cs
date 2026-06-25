namespace Custodex.Abstractions;

/// <summary>
/// A reference to a subject — the party a permission is evaluated for. A plain subject is a single
/// principal (<c>type:id</c>); when <paramref name="Relation"/> is set it is a <em>subject set</em>
/// (userset), <c>type:id#relation</c>, standing for every subject related to that object by that relation.
/// </summary>
/// <param name="Type">The subject's entity type as declared in the schema; compared ordinally.</param>
/// <param name="Id">The instance identifier, or the reserved wildcard <c>"*"</c>.</param>
/// <param name="Relation">When set, names the relation whose members this reference expands to, making it a subject set.</param>
public readonly record struct SubjectRef(string Type, string Id, string? Relation = null)
{
    /// <summary>Whether this reference is a subject set (<c>type:id#relation</c>) rather than a single principal.</summary>
    public bool IsSubjectSet => Relation is not null;

    /// <summary>
    /// Whether this reference is the reserved wildcard (<c>type:*</c>): it matches every subject of
    /// <see cref="Type"/>, the way a tuple grants a relation to an entire type.
    /// </summary>
    public bool IsWildcard => Id == "*";

    /// <summary>Returns the canonical rendering: <c>type:id</c>, or <c>type:id#relation</c> for a subject set.</summary>
    public override string ToString() => Relation is null ? $"{Type}:{Id}" : $"{Type}:{Id}#{Relation}";
}
