using Custodex.Abstractions;

namespace Custodex.Studio.Components.Schema;

/// <summary>The role a <see cref="SchemaGraphNode"/> plays in the permission model.</summary>
public enum SchemaNodeKind
{
    /// <summary>An entity type declared by the schema.</summary>
    Type,

    /// <summary>A relation declared on an entity type.</summary>
    Relation,

    /// <summary>A permission computed on an entity type.</summary>
    Permission
}

/// <summary>The meaning a <see cref="SchemaGraphEdge"/> carries between two nodes.</summary>
public enum SchemaEdgeKind
{
    /// <summary>A type declares a relation.</summary>
    DeclaresRelation,

    /// <summary>A type declares a permission.</summary>
    DeclaresPermission,

    /// <summary>A relation accepts a subject shape — a type, a subject set, or a wildcard.</summary>
    AllowsSubject,

    /// <summary>A permission expression names a relation or permission it is computed from.</summary>
    References
}

/// <summary>A node in the projected schema graph: an entity type, a relation, or a permission.</summary>
/// <param name="Id">The stable, deterministic node identifier; each node is prefixed by its kind (<c>type:</c>, <c>relation:</c>, or <c>permission:</c>) so identifiers are unique across all three kinds.</param>
/// <param name="Label">The display label — the bare type, relation, or permission name.</param>
/// <param name="Kind">The role this node plays in the model.</param>
public sealed record SchemaGraphNode(string Id, string Label, SchemaNodeKind Kind);

/// <summary>A directed edge between two <see cref="SchemaGraphNode"/> identifiers.</summary>
/// <param name="FromId">The source node identifier.</param>
/// <param name="ToId">The target node identifier.</param>
/// <param name="Kind">The relationship the edge represents.</param>
public sealed record SchemaGraphEdge(string FromId, string ToId, SchemaEdgeKind Kind);

/// <summary>A node-and-edge view of a <see cref="Custodex.Abstractions.Schema"/> suitable for tree and graph rendering.</summary>
/// <param name="Nodes">The projected nodes.</param>
/// <param name="Edges">The projected edges, deduplicated.</param>
public sealed record SchemaGraph(IReadOnlyList<SchemaGraphNode> Nodes, IReadOnlyList<SchemaGraphEdge> Edges);

/// <summary>
/// Projects a <see cref="Custodex.Abstractions.Schema"/> into a deterministic node-and-edge graph: a node per type, relation
/// and permission, with edges for relation and permission declarations, the subject shapes a relation
/// accepts, and the relations a permission expression references. Identifiers are derived purely from
/// schema names, so the same schema always yields the same graph. References to names the schema does
/// not declare are tolerated: the edge is still emitted to the best-effort qualified identifier rather
/// than throwing.
/// </summary>
public static class SchemaGraphProjection
{
    /// <summary>Projects <paramref name="schema"/> into its graph representation.</summary>
    /// <param name="schema">The schema to project.</param>
    /// <returns>The projected nodes and deduplicated edges.</returns>
    public static SchemaGraph Project(Custodex.Abstractions.Schema schema)
    {
        List<SchemaGraphNode> nodes = [];
        HashSet<string> nodeIds = new(StringComparer.Ordinal);

        void AddNode(string id, string label, SchemaNodeKind kind)
        {
            if (nodeIds.Add(id))
                nodes.Add(new SchemaGraphNode(id, label, kind));
        }

        foreach (var type in schema.Types)
        {
            AddNode(TypeId(type.Name), type.Name, SchemaNodeKind.Type);

            foreach (var relation in type.Relations)
                AddNode(RelationId(type.Name, relation.Name), relation.Name, SchemaNodeKind.Relation);

            foreach (var permission in type.Permissions)
                AddNode(PermissionId(type.Name, permission.Name), permission.Name, SchemaNodeKind.Permission);
        }

        List<SchemaGraphEdge> edges = [];
        HashSet<SchemaGraphEdge> seen = [];

        void AddEdge(string fromId, string toId, SchemaEdgeKind kind)
        {
            var edge = new SchemaGraphEdge(fromId, toId, kind);
            if (seen.Add(edge))
                edges.Add(edge);
        }

        foreach (var type in schema.Types)
        {
            var typeId = TypeId(type.Name);

            foreach (var relation in type.Relations)
            {
                var relationId = RelationId(type.Name, relation.Name);
                AddEdge(typeId, relationId, SchemaEdgeKind.DeclaresRelation);

                foreach (var subject in relation.AllowedSubjects)
                {
                    var target = subject.Relation is { } subjectRelation && nodeIds.Contains(RelationId(subject.Type, subjectRelation))
                        ? RelationId(subject.Type, subjectRelation)
                        : TypeId(subject.Type);
                    AddEdge(relationId, target, SchemaEdgeKind.AllowsSubject);
                }
            }

            foreach (var permission in type.Permissions)
            {
                var permissionId = PermissionId(type.Name, permission.Name);
                AddEdge(typeId, permissionId, SchemaEdgeKind.DeclaresPermission);

                foreach (var relationName in ReferencedRelations(permission.Expression))
                    AddEdge(permissionId, RelationId(type.Name, relationName), SchemaEdgeKind.References);
            }
        }

        return new SchemaGraph(nodes, edges);
    }

    private static IEnumerable<string> ReferencedRelations(PermExpr expr) => expr switch
    {
        RelationRef r => [r.Relation],
        Arrow a => [a.Relation],
        Union u => [.. ReferencedRelations(u.Left), .. ReferencedRelations(u.Right)],
        Intersect i => [.. ReferencedRelations(i.Left), .. ReferencedRelations(i.Right)],
        Exclude e => [.. ReferencedRelations(e.Left), .. ReferencedRelations(e.Right)],
        Conditioned c => ReferencedRelations(c.Inner),
        _ => throw new ArgumentOutOfRangeException(nameof(expr), expr, "Unrecognised permission expression node.")
    };

    private static string TypeId(string type) => $"type:{type}";

    private static string RelationId(string type, string relation) => $"relation:{type}.{relation}";

    private static string PermissionId(string type, string permission) => $"permission:{type}.{permission}";
}
