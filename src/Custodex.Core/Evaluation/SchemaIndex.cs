using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed class SchemaIndex
{
    private readonly Dictionary<string, EntityTypeDef> _types;
    private readonly Dictionary<(string Type, string Name), PermissionDef> _permissions;
    private readonly Dictionary<(string Type, string Name), RelationDef> _relations;
    private readonly Dictionary<string, ConditionDef> _conditions;

    public SchemaIndex(Schema schema)
    {
        Schema = schema;
        _types = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _permissions = new();
        _relations = new();
        foreach (var t in schema.Types)
        {
            foreach (var p in t.Permissions) _permissions[(t.Name, p.Name)] = p;
            foreach (var r in t.Relations) _relations[(t.Name, r.Name)] = r;
        }
        _conditions = schema.Conditions.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    public Schema Schema { get; }

    public EntityTypeDef Type(string name) =>
        _types.TryGetValue(name, out var t) ? t : throw new UnknownTypeException(name);

    public PermissionDef Permission(string type, string perm)
    {
        if (!_types.ContainsKey(type)) throw new UnknownTypeException(type);
        return _permissions.TryGetValue((type, perm), out var p)
            ? p : throw new UnknownPermissionException(type, perm);
    }

    public bool TryPermission(string type, string perm, out PermissionDef def) =>
        _permissions.TryGetValue((type, perm), out def!);

    public RelationDef Relation(string type, string rel)
    {
        if (!_types.ContainsKey(type)) throw new UnknownTypeException(type);
        return _relations.TryGetValue((type, rel), out var r)
            ? r : throw new UnknownRelationException(type, rel);
    }

    public bool TryRelation(string type, string rel, out RelationDef def) =>
        _relations.TryGetValue((type, rel), out def!);

    public ConditionDef Condition(string name) =>
        _conditions.TryGetValue(name, out var c)
            ? c : throw new UnknownPermissionException("condition", name);
}
