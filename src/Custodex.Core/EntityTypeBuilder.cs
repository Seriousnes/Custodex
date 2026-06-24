using Custodex.Abstractions;

namespace Custodex.Core;

public sealed class EntityTypeBuilder
{
    private readonly List<RelationDef> _relations = [];
    private readonly List<PermissionDef> _permissions = [];

    public EntityTypeBuilder Relation(string name, Action<SubjectFillerBuilder> fillers)
    {
        var b = new SubjectFillerBuilder();
        fillers(b);
        _relations.Add(new RelationDef(name, b.Build()));
        return this;
    }

    public EntityTypeBuilder Permission(string name, Action<PermExprBuilder> expr)
    {
        var b = new PermExprBuilder();
        expr(b);
        _permissions.Add(new PermissionDef(name, b.Build()));
        return this;
    }

    internal (IReadOnlyList<RelationDef> Relations, IReadOnlyList<PermissionDef> Permissions) Build() => (_relations, _permissions);
}
