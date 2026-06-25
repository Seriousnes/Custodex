namespace Custodex.Abstractions;

/// <summary>
/// A reference to an object (resource) in the permission graph, identified by its
/// <paramref name="Type"/> and <paramref name="Id"/> and rendered as <c>type:id</c>.
/// </summary>
/// <param name="Type">The entity type as declared in the schema; compared ordinally.</param>
/// <param name="Id">The instance identifier within that type, or the reserved wildcard <c>"*"</c>.</param>
public readonly record struct EntityRef(string Type, string Id)
{
    /// <summary>
    /// Whether this reference is the reserved wildcard (<c>type:*</c>): it denotes every instance of
    /// <see cref="Type"/> within the tenant rather than a single object.
    /// </summary>
    public bool IsWildcard => Id == "*";

    /// <summary>Returns the canonical <c>type:id</c> rendering of this reference.</summary>
    public override string ToString() => $"{Type}:{Id}";
}
