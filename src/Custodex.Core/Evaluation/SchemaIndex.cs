using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

/// <summary>
/// Wraps a <see cref="Schema"/> in ordinal lookup dictionaries for its types, permissions,
/// relations, and conditions. Lookups resolve a definition or throw a typed
/// <c>Unknown*Exception</c>; the <c>Try*</c> variants report absence instead.
/// </summary>
public sealed class SchemaIndex
{
    private readonly Dictionary<string, EntityTypeDef> _types;
    private readonly Dictionary<(string Type, string Name), PermissionDef> _permissions;
    private readonly Dictionary<(string Type, string Name), RelationDef> _relations;
    private readonly Dictionary<string, ConditionDef> _conditions;

    /// <summary>Builds the lookup tables from the given schema.</summary>
    /// <param name="schema">The schema whose types, permissions, relations, and conditions are indexed.</param>
    public SchemaIndex(Schema schema)
    {
        Schema = schema;
        _types = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _permissions = [];
        _relations = [];
        foreach (var t in schema.Types)
        {
            foreach (var p in t.Permissions) _permissions[(t.Name, p.Name)] = p;
            foreach (var r in t.Relations) _relations[(t.Name, r.Name)] = r;
        }
        _conditions = schema.Conditions.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>The schema this index was built from.</summary>
    public Schema Schema { get; }

    /// <summary>
    /// Whether the schema declares at least one condition. When false, no tuple anywhere in the
    /// tenant can carry a condition, so a provider may take a condition-unaware reachability
    /// shortcut; when true, a conditioned tuple may appear at any depth and conditions must be
    /// evaluated per level.
    /// </summary>
    public bool HasConditions => _conditions.Count > 0;

    /// <summary>Resolves an entity type by name.</summary>
    /// <param name="name">The entity type name.</param>
    /// <returns>The matching type definition.</returns>
    /// <exception cref="UnknownTypeException">No type with that name exists in the schema.</exception>
    public EntityTypeDef Type(string name) =>
        _types.TryGetValue(name, out var t) ? t : throw new UnknownTypeException(name);

    /// <summary>Resolves a permission declared on an entity type.</summary>
    /// <param name="type">The entity type the permission is declared on.</param>
    /// <param name="perm">The permission name.</param>
    /// <returns>The matching permission definition.</returns>
    /// <exception cref="UnknownTypeException">The entity type does not exist.</exception>
    /// <exception cref="UnknownPermissionException">The type has no permission with that name.</exception>
    public PermissionDef Permission(string type, string perm)
    {
        if (!_types.ContainsKey(type)) throw new UnknownTypeException(type);
        return _permissions.TryGetValue((type, perm), out var p)
            ? p : throw new UnknownPermissionException(type, perm);
    }

    /// <summary>Attempts to resolve a permission without throwing when it is absent.</summary>
    /// <param name="type">The entity type the permission is declared on.</param>
    /// <param name="perm">The permission name.</param>
    /// <param name="def">The resolved permission definition when found.</param>
    /// <returns><c>true</c> if the permission exists; otherwise <c>false</c>.</returns>
    public bool TryPermission(string type, string perm, out PermissionDef def) =>
        _permissions.TryGetValue((type, perm), out def!);

    /// <summary>Resolves a relation declared on an entity type.</summary>
    /// <param name="type">The entity type the relation is declared on.</param>
    /// <param name="rel">The relation name.</param>
    /// <returns>The matching relation definition.</returns>
    /// <exception cref="UnknownTypeException">The entity type does not exist.</exception>
    /// <exception cref="UnknownRelationException">The type has no relation with that name.</exception>
    public RelationDef Relation(string type, string rel)
    {
        if (!_types.ContainsKey(type)) throw new UnknownTypeException(type);
        return _relations.TryGetValue((type, rel), out var r)
            ? r : throw new UnknownRelationException(type, rel);
    }

    /// <summary>Attempts to resolve a relation without throwing when it is absent.</summary>
    /// <param name="type">The entity type the relation is declared on.</param>
    /// <param name="rel">The relation name.</param>
    /// <param name="def">The resolved relation definition when found.</param>
    /// <returns><c>true</c> if the relation exists; otherwise <c>false</c>.</returns>
    public bool TryRelation(string type, string rel, out RelationDef def) =>
        _relations.TryGetValue((type, rel), out def!);

    /// <summary>Attempts to resolve an entity type without throwing when it is absent.</summary>
    /// <param name="name">The entity type name.</param>
    /// <param name="def">The resolved type definition when found.</param>
    /// <returns><c>true</c> if the type exists; otherwise <c>false</c>.</returns>
    public bool TryType(string name, out EntityTypeDef def) => _types.TryGetValue(name, out def!);

    /// <summary>Resolves a named condition.</summary>
    /// <param name="name">The condition name.</param>
    /// <returns>The matching condition definition.</returns>
    /// <exception cref="UnknownPermissionException">No condition with that name is defined.</exception>
    public ConditionDef Condition(string name) =>
        _conditions.TryGetValue(name, out var c)
            ? c : throw new UnknownPermissionException("condition", name);
}
