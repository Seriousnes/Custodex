using Relkit.Abstractions;

namespace Relkit.Core;

public sealed class PermExprBuilder
{
    private PermExpr? _current;

    private PermExprBuilder Add(PermExpr node)
    {
        _current = _current is null ? node : new Union(_current, node);
        return this;
    }

    public PermExprBuilder Relation(string relation) => Add(new RelationRef(relation));
    public PermExprBuilder Arrow(string relation, string permission) => Add(new Arrow(relation, permission));

    public PermExprBuilder Union(Action<PermExprBuilder> build) => Add(BuildSub(build));

    public PermExprBuilder Intersect(Action<PermExprBuilder> build)
    {
        _current = new Intersect(Require(), BuildSub(build));
        return this;
    }

    public PermExprBuilder Exclude(Action<PermExprBuilder> build)
    {
        _current = new Exclude(Require(), BuildSub(build));
        return this;
    }

    public PermExprBuilder Conditioned(string conditionName)
    {
        _current = new Conditioned(Require(), conditionName);
        return this;
    }

    public PermExpr Build() => Require();

    private PermExpr Require() => _current ?? throw new InvalidOperationException("Permission expression has no terms.");

    private static PermExpr BuildSub(Action<PermExprBuilder> build)
    {
        var sub = new PermExprBuilder();
        build(sub);
        return sub.Build();
    }
}
