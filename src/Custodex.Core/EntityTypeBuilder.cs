using Custodex.Abstractions;

namespace Custodex.Core;

/// <summary>Fluent builder for one entity type, accumulating its relations (the stored edges) and permissions (computed over them).</summary>
public sealed class EntityTypeBuilder
{
    private readonly List<RelationDef> _relations = [];
    private readonly List<PermissionDef> _permissions = [];

    /// <summary>Declares a relation on this type and the subject shapes a tuple on it may name.</summary>
    /// <param name="name">The relation name, used as the stored edge label and referenced by permission expressions.</param>
    /// <param name="fillers">Callback that declares which subject types, subject sets, or wildcards the relation accepts.</param>
    /// <returns>This builder, for chaining.</returns>
    public EntityTypeBuilder Relation(string name, Action<SubjectFillerBuilder> fillers)
    {
        var b = new SubjectFillerBuilder();
        fillers(b);
        _relations.Add(new RelationDef(name, b.Build()));
        return this;
    }

    /// <summary>Declares a permission on this type and the expression that computes who holds it.</summary>
    /// <param name="name">The permission name, queried by Check and the list operations.</param>
    /// <param name="expr">Callback that builds the permission expression over this type's relations.</param>
    /// <returns>This builder, for chaining.</returns>
    public EntityTypeBuilder Permission(string name, Action<PermExprBuilder> expr)
    {
        var b = new PermExprBuilder();
        expr(b);
        _permissions.Add(new PermissionDef(name, b.Build()));
        return this;
    }

    internal (IReadOnlyList<RelationDef> Relations, IReadOnlyList<PermissionDef> Permissions) Build() => (_relations, _permissions);
}
